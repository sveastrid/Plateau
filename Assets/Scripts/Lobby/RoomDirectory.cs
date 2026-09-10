using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

/// <summary>
/// The public-room noticeboard. Unity Lobby is a **directory over the existing Relay flow** and
/// nothing more: the host publishes its Relay join code into a lobby, and a browsing client reads
/// the code back out and then goes down RelayVivox.JoinRelay(code) completely unchanged.
///
/// That is the whole reason not to use the Sessions API instead. Sessions would replace RelayVivox
/// wholesale — the connectInFlight latch, the 15 s watchdog, the dtls match, the Shutdown-before-
/// retry fix — and re-open docs/quest_networking_plan.md in new code.
///
/// It lives on the **Network Manager** object, which Netcode marks DontDestroyOnLoad, because the
/// heartbeat below has to keep running after the host has left the lobby scene for a game.
///
/// Four things decide whether this works in practice, and all four are the service's rules rather
/// than C#:
///
///  1. **Heartbeat or the room vanishes.** A lobby is deleted after roughly 30 s without a ping.
///     That timeout is a feature: a host that crashes cannot leave a ghost room in the browser for
///     ever, which is the failure everyone hits when they clean up only on a graceful exit.
///  2. **Rate limits are per-lobby and tight.** Query is throttled and disabled while one is in
///     flight; the player count is written by the host only and coalesced.
///  3. **Publish the build and grey out rooms this build cannot join.** ForceSamePrefabs refuses a
///     mismatched build with no reason string, and the joiner just sees "Joining room..." for ever.
///     Filtering on `build` turns this project's most mystifying failure into a row that says
///     "Different version". This is the highest-value line in the file.
///  4. **Degrade, do not block.** If Lobby is unreachable the two public rows go inert with a
///     reason and private rooms keep working. Same principle as the anchoring code.
///
/// Prerequisite, once: Lobby must be enabled for this project in the Unity Cloud dashboard, as
/// Relay and Vivox already are. Same project id, same anonymous sign-in RelayVivox.Start performs.
/// </summary>
public class RoomDirectory : MonoBehaviour
{
    public static RoomDirectory Instance { get; private set; }

    // Data keys. Indexed so the query can filter server-side rather than pulling every lobby.
    const string JoinCodeKey = "joinCode";
    const string GameKeyKey = "gameKey";
    const string BuildKey = "build";
    const string HostKey = "host";
    const string PlayersKey = "players";

    [Tooltip("Seconds between heartbeat pings. A lobby is deleted after about 30 s without one.")]
    public float heartbeatSeconds = 15f;

    [Tooltip("At most one UpdateLobbyAsync this often. Lobby allows roughly 5 updates per 5 s.")]
    public float playerCountCoalesceSeconds = 6f;

    [Tooltip("Minimum gap between queries. Lobby allows roughly one per second.")]
    public float queryCooldownSeconds = 1.2f;

    /// <summary>False once a call has failed. The Play panel greys its public rows and says why.</summary>
    public bool Available { get; private set; } = true;

    public string LastError { get; private set; }

    /// <summary>The lobby this client is hosting, or null.</summary>
    public string PublishedLobbyId { get; private set; }

    private Coroutine heartbeat;
    private float nextQueryAllowed;
    private bool queryInFlight;
    private float nextCountUpdate;
    private bool countDirty;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ------------------------------------------------------------------ hosting

    /// <summary>
    /// List the room. Called by the host immediately after CreateRelay has a join code, and never
    /// for a private room.
    /// </summary>
    public async Task<bool> PublishAsync(string roomName, string joinCode, string gameKey,
                                         string hostName, int maxPlayers)
    {
        if (string.IsNullOrEmpty(joinCode))
        {
            return false;
        }

        try
        {
            CreateLobbyOptions options = new CreateLobbyOptions
            {
                IsPrivate = false,
                Data = new Dictionary<string, DataObject>
                {
                    [JoinCodeKey] = new DataObject(DataObject.VisibilityOptions.Public, joinCode,
                                                   DataObject.IndexOptions.S1),
                    [GameKeyKey] = new DataObject(DataObject.VisibilityOptions.Public, gameKey ?? "",
                                                  DataObject.IndexOptions.S2),
                    [BuildKey] = new DataObject(DataObject.VisibilityOptions.Public,
                                                Application.version ?? "",
                                                DataObject.IndexOptions.S3),
                    [HostKey] = new DataObject(DataObject.VisibilityOptions.Public, hostName ?? ""),
                    [PlayersKey] = new DataObject(DataObject.VisibilityOptions.Public, "1",
                                                  DataObject.IndexOptions.N1),
                }
            };

            string name = string.IsNullOrEmpty(roomName) ? (hostName + "'s room") : roomName;
            Unity.Services.Lobbies.Models.Lobby lobby =
                await LobbyService.Instance.CreateLobbyAsync(name, Mathf.Clamp(maxPlayers, 2, 12), options);

            PublishedLobbyId = lobby.Id;
            Available = true;
            LastError = null;

            StartHeartbeat();
            WatchPlayerCount();
            return true;
        }
        catch (Exception e)
        {
            Fail("Could not list the room", e);
            return false;
        }
    }

    /// <summary>
    /// Take the room out of the directory. Called when the host leaves. The 30 s timeout is the
    /// backstop for every path that never reaches here, which is most of them.
    /// </summary>
    public async Task UnpublishAsync()
    {
        StopHeartbeat();

        string id = PublishedLobbyId;
        PublishedLobbyId = null;

        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        try
        {
            await LobbyService.Instance.DeleteLobbyAsync(id);
        }
        catch (Exception e)
        {
            // Nothing to recover: the lobby expires on its own within 30 s.
            Debug.LogWarning("RoomDirectory: could not delete lobby " + id + ". It will expire. " + e);
        }
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        heartbeat = StartCoroutine(Heartbeat());
    }

    private void StopHeartbeat()
    {
        if (heartbeat != null)
        {
            StopCoroutine(heartbeat);
            heartbeat = null;
        }
    }

    private IEnumerator Heartbeat()
    {
        WaitForSeconds wait = new WaitForSeconds(Mathf.Max(5f, heartbeatSeconds));

        while (!string.IsNullOrEmpty(PublishedLobbyId))
        {
            yield return wait;

            if (string.IsNullOrEmpty(PublishedLobbyId))
            {
                yield break;
            }

            _ = PingAsync(PublishedLobbyId);
        }
    }

    private async Task PingAsync(string id)
    {
        try
        {
            await LobbyService.Instance.SendHeartbeatPingAsync(id);
        }
        catch (Exception e)
        {
            // Do not tear the room down on one failed ping — a dropped packet is not a dead lobby,
            // and the next tick is 15 s away against a 30 s timeout.
            Debug.LogWarning("RoomDirectory: heartbeat failed. " + e.Message);
        }
    }

    // ------------------------------------------------------------------ the player count

    /// <summary>
    /// The host, and only the host, writes the count. The alternative — every joiner also joining
    /// the Lobby so AvailableSlots maintains itself — doubles the heartbeat and leave failure
    /// surface for the sake of one number.
    /// </summary>
    private void WatchPlayerCount()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return;
        }

        nm.OnClientConnectedCallback += MarkCountDirty;
        nm.OnClientDisconnectCallback += MarkCountDirty;
    }

    private void MarkCountDirty(ulong clientId)
    {
        countDirty = true;
    }

    void Update()
    {
        if (!countDirty || string.IsNullOrEmpty(PublishedLobbyId))
        {
            return;
        }

        if (Time.unscaledTime < nextCountUpdate)
        {
            return;                           // coalesced; the flag stays set
        }

        countDirty = false;
        nextCountUpdate = Time.unscaledTime + Mathf.Max(2f, playerCountCoalesceSeconds);
        _ = PushCountAsync();
    }

    private async Task PushCountAsync()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || string.IsNullOrEmpty(PublishedLobbyId))
        {
            return;
        }

        int count = nm.ConnectedClientsList != null ? nm.ConnectedClientsList.Count : 1;

        try
        {
            await LobbyService.Instance.UpdateLobbyAsync(PublishedLobbyId, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    [PlayersKey] = new DataObject(DataObject.VisibilityOptions.Public,
                                                  count.ToString(), DataObject.IndexOptions.N1),
                }
            });
        }
        catch (Exception e)
        {
            Debug.LogWarning("RoomDirectory: could not update the player count. " + e.Message);
        }
    }

    // ------------------------------------------------------------------ browsing

    /// <summary>True when a Refresh press should be accepted. The panel greys the row otherwise.</summary>
    public bool CanQuery => Available && !queryInFlight && Time.unscaledTime >= nextQueryAllowed;

    /// <summary>
    /// Every listed room, newest first, with the ones this build or this library cannot join marked
    /// rather than hidden. Hiding them would leave a player who bought nothing staring at an empty
    /// browser with no idea why.
    /// </summary>
    public async Task<List<RoomListing>> QueryAsync()
    {
        List<RoomListing> rooms = new List<RoomListing>();

        if (queryInFlight)
        {
            return rooms;
        }

        queryInFlight = true;
        nextQueryAllowed = Time.unscaledTime + Mathf.Max(0.5f, queryCooldownSeconds);

        try
        {
            QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
            {
                Count = 25,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0",
                                    QueryFilter.OpOptions.GT),
                }
            });

            Available = true;
            LastError = null;

            foreach (Unity.Services.Lobbies.Models.Lobby lobby in response.Results)
            {
                rooms.Add(Read(lobby));
            }
        }
        catch (Exception e)
        {
            Fail("Public rooms are unavailable", e);
        }
        finally
        {
            queryInFlight = false;
        }

        return rooms;
    }

    private RoomListing Read(Unity.Services.Lobbies.Models.Lobby lobby)
    {
        RoomListing room = new RoomListing
        {
            id = lobby.Id,
            name = lobby.Name,
            maxPlayers = lobby.MaxPlayers,
            players = lobby.MaxPlayers - lobby.AvailableSlots,
        };

        if (lobby.Data != null)
        {
            room.joinCode = Value(lobby, JoinCodeKey);
            room.gameKey = Value(lobby, GameKeyKey);
            room.build = Value(lobby, BuildKey);
            room.hostName = Value(lobby, HostKey);

            // The host's own count is more accurate than AvailableSlots, which only moves when
            // somebody joins the *Lobby* — and in this design nobody but the host ever does.
            if (int.TryParse(Value(lobby, PlayersKey), out int published))
            {
                room.players = published;
            }
        }

        return room;
    }

    private static string Value(Unity.Services.Lobbies.Models.Lobby lobby, string key)
    {
        return lobby.Data != null && lobby.Data.TryGetValue(key, out DataObject data) ? data.Value : "";
    }

    private void Fail(string message, Exception e)
    {
        Available = false;
        LastError = message;
        Debug.LogWarning("RoomDirectory: " + message + ". " + e.Message);
    }
}

/// <summary>One row in the browser. Everything needed to draw it and to join it.</summary>
public class RoomListing
{
    public string id = "";
    public string name = "";
    public string hostName = "";
    public string gameKey = "";
    public string joinCode = "";
    public string build = "";
    public int players;
    public int maxPlayers;

    public bool Full => maxPlayers > 0 && players >= maxPlayers;

    public bool SameBuild => build == (Application.version ?? "");
}
