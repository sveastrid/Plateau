using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Vivox;
using System.Threading.Tasks;
using Unity.Networking.Transport.Relay;

public class RelayVivox : MonoBehaviour
{
    public string relayRoomCode;
    public string myUserDisplayName;
    public bool roomOwner;

    [Tooltip("Relay transport protocol: \"dtls\" (encrypted UDP, the default and the only one " +
             "Unity's current NGO Relay docs describe), \"udp\" (plain), or \"wss\". A field " +
             "rather than a literal so both can be tried in one session without a rebuild — see " +
             "docs/quest_networking_plan.md. It MUST match on the host and the joiner.")]
    public string connectionType = "dtls";

    // Unity Services sign-in is asynchronous and used to be fire-and-forget: a headset that came up
    // without a network signed in silently-never, the player could still type a room code, and the
    // RelayService call then failed in a way GameController does not catch.
    private bool servicesReady;

    // Start is called before the first frame update
    private async void Start()
    {
        try
        {
            await UnityServices.InitializeAsync();

            AuthenticationService.Instance.SignedIn += () => {
                Debug.Log("Signed in " + AuthenticationService.Instance.PlayerId);
            };

            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            servicesReady = true;
        }
        catch (Exception e)
        {
            // async void: an exception here has nowhere to go and would vanish without a trace.
            Debug.LogError("RelayVivox: Unity Services sign-in failed. Hosting and joining will " +
                           "not work. Is the headset on a network? " + e);
        }
    }

    public async Task StartRelayAndVivox(string userDisplayName)
    {
        myUserDisplayName = userDisplayName;
        await CreateRelay();
    }

    public async Task JoinRelayAndVivox(string userDisplayName, string joinCode)
    {
        myUserDisplayName = userDisplayName;
        await JoinRelay(joinCode);
    }

    /// <summary>
    /// Says so when the room is about to be built on a sign-in that never happened. Deliberately
    /// only a log: RelayService itself throws a RelayServiceException when it is not authenticated,
    /// and GameController already recovers from that — this just names the real cause first, so the
    /// in-headset box says "no network at launch" rather than only "Wrong Room Code".
    /// </summary>
    private void RequireServices()
    {
        if (!servicesReady)
        {
            Debug.LogWarning("RelayVivox: Unity Services never finished signing in. The relay call " +
                             "below is expected to fail.");
        }
    }

    public async Task CreateRelay()
    {
        RequireServices();

        try
        {
            roomOwner = true;
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(12);
            relayRoomCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log(relayRoomCode);

            Debug.Log("RelayVivox: hosting over \"" + connectionType + "\".");
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(allocation.ToRelayServerData(connectionType));

            /*Old Method
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetHostRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData
            );
            */
            NetworkManager.Singleton.StartHost();
        }
        catch (RelayServiceException e)
        {
            Debug.Log(e);
            throw;          // GameController has to know the room was never created.
        }
    }

    public async Task JoinRelay(string joinCode)
    {
        RequireServices();

        try
        {
            roomOwner = false;
            relayRoomCode = joinCode;
            Debug.Log("Joining Relay with " + joinCode);
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            Debug.Log($"Configuring transport. Protocol is: {transport.Protocol}");

            Debug.Log("RelayVivox: joining over \"" + connectionType + "\".");
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(joinAllocation.ToRelayServerData(connectionType));
            /* Old Method
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetClientRelayData(
                joinAllocation.RelayServer.IpV4,
                (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes,
                joinAllocation.Key,
                joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData
            );
            */
            NetworkManager.Singleton.StartClient();
        }
        catch (RelayServiceException e)
        {
            Debug.Log(e);
            throw e;
        }
    }

    // ------------------------------------------------------------------ voice

    // What the room has asked for, and what this client has actually done about it. Voice is no
    // longer started with the session: RoomAnchor.voiceEnabled drives it, so that a room where
    // everybody is sitting at the same table never opens a Vivox session at all.
    private bool desiredVoiceOn;
    private bool voiceIsConnected;
    private bool reconciling;

    /// <summary>
    /// Bring voice into line with what the room has asked for. Safe to call repeatedly, and safe
    /// to call while an earlier transition is still in flight.
    /// </summary>
    public void ApplyVoiceState(bool on)
    {
        desiredVoiceOn = on;

        // One reconcile at a time. A second call while the first is awaiting has already updated
        // desiredVoiceOn, and the loop below will pick it up.
        if (!reconciling)
        {
            _ = ReconcileVoiceAsync();
        }
    }

    /// <summary>
    /// Drive the connection towards desiredVoiceOn. This loops rather than acting once because the
    /// target can change while a connect or disconnect is awaiting — the host toggling twice in
    /// quick succession must end in the state of the last press, not the first.
    /// </summary>
    private async Task ReconcileVoiceAsync()
    {
        reconciling = true;
        try
        {
            while (desiredVoiceOn != voiceIsConnected)
            {
                bool ok = desiredVoiceOn
                              ? await ConnectVoiceAsync()
                              : await DisconnectVoiceAsync();
                if (!ok)
                {
                    break;      // a service that is failing must not be retried in a tight loop
                }
            }
        }
        finally
        {
            reconciling = false;
        }
    }

    private async Task<bool> ConnectVoiceAsync()
    {
        if (string.IsNullOrEmpty(relayRoomCode))
        {
            Debug.LogWarning("RelayVivox: voice was asked for before a relay room exists. Ignoring.");
            return false;
        }

        try
        {
            Debug.Log("RelayVivox: connecting voice to channel " + relayRoomCode);

            // Initialization is per-process and a logout does not undo it, so a second connect has
            // to skip this — InitializeAsync throws if it has already run.
            if (VivoxService.Instance.InitializationState != VivoxInitializationState.Initialized)
            {
                await VivoxService.Instance.InitializeAsync();
            }

            if (!VivoxService.Instance.IsLoggedIn)
            {
                LoginOptions options = new LoginOptions();
                options.DisplayName = myUserDisplayName;
                options.EnableTTS = true;
                await VivoxService.Instance.LoginAsync(options);
            }

            // The channel is named after the relay join code, so everyone in the session lands in
            // the same one without anything extra being replicated.
            await VivoxService.Instance.JoinGroupChannelAsync(relayRoomCode, ChatCapability.AudioOnly, null);
            voiceIsConnected = true;
            return true;
        }
        catch (Exception e)
        {
            // The caller is a NetworkVariable callback and cannot await this, so an exception that
            // is not caught here vanishes without a trace.
            Debug.LogError("RelayVivox: could not connect voice. " + e);
            return false;
        }
    }

    private async Task<bool> DisconnectVoiceAsync()
    {
        try
        {
            Debug.Log("RelayVivox: disconnecting voice.");
            await VivoxService.Instance.LeaveAllChannelsAsync();

            // Logging out as well as leaving the channel: staying logged in holds a Vivox session
            // open, which is the thing this whole toggle exists to avoid.
            if (VivoxService.Instance.IsLoggedIn)
            {
                await VivoxService.Instance.LogoutAsync();
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("RelayVivox: could not cleanly disconnect voice. " + e);
            return true;      // report done anyway; see the finally below
        }
        finally
        {
            // Disconnected either way. A failed logout must not leave this stuck believing it is
            // still connected, or the host can never switch voice back on.
            voiceIsConnected = false;
        }
    }
}
