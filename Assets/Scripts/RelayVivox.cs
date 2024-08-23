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
    // Start is called before the first frame update
    private async void Start()
    {
        await UnityServices.InitializeAsync();

        AuthenticationService.Instance.SignedIn += () => {
            Debug.Log("Signed in " + AuthenticationService.Instance.PlayerId);
        };

        await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    public async void StartRelayAndVivox(string userDisplayName)
    {
        myUserDisplayName = userDisplayName;
        await CreateRelay();
        startVivoxVoice(userDisplayName, relayRoomCode);
    }

    public async Task JoinRelayAndVivox(string userDisplayName, string joinCode)
    {
        myUserDisplayName = userDisplayName;
        await JoinRelay(joinCode);
        startVivoxVoice(userDisplayName, joinCode);
    }

    public async Task CreateRelay()
    {
        try
        {
            roomOwner = true;
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(12);
            relayRoomCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log(relayRoomCode);

            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(allocation.ToRelayServerData("dtls"));

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
        }
    }

    public async Task JoinRelay(string joinCode)
    {
        try
        {
            roomOwner = false;
            relayRoomCode = joinCode;
            Debug.Log("Joining Relay with " + joinCode);
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            Debug.Log($"Configuring transport. Protocol is: {transport.Protocol}");

            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(joinAllocation.ToRelayServerData("dtls"));
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

    public async void startVivoxVoice(string userDisplayName, string roomCode)
    {
        await InitializeVivoxAsync();
        await LoginToVivoxAsync(userDisplayName);
        await VivoxService.Instance.JoinGroupChannelAsync(roomCode, ChatCapability.AudioOnly, null);
    }

    public async Task InitializeVivoxAsync()
    {
        await VivoxService.Instance.InitializeAsync();
    }

    public async Task LoginToVivoxAsync(string userDisplayName)
    {
        LoginOptions options = new LoginOptions();
        options.DisplayName = userDisplayName;
        options.EnableTTS = true;
        await VivoxService.Instance.LoginAsync(options);
    }
}
