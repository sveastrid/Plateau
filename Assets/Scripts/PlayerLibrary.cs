using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Which games this player brought to the room, as a bit per <see cref="GameModule.libraryBit"/>.
///
/// **Server write permission**, matching playerName, spawnSlot and roomOwner — every fact about a
/// player that the room rather than the player decides. The value comes out of the connection
/// approval payload, so the server sets it once at spawn and no client ever writes it.
///
/// A ulong rather than a NetworkList of product ids: 8 bytes, no allocation, and it is read on
/// every menu open by RoomLibrary.Union.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PlayerLibrary : NetworkBehaviour
{
    public NetworkVariable<ulong> ownedMask = new NetworkVariable<ulong>(
        0UL, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            return;
        }

        if (RoomApproval.Instance != null &&
            RoomApproval.Instance.TryGet(OwnerClientId, out ConnectionPayload payload))
        {
            ownedMask.Value = payload.ownedMask;
            return;
        }

        // No approval record. That is not an error state worth refusing a player over — a room
        // whose host never ran the lobby (a scene opened directly in the Editor) still has to work
        // — so an unknown library reads as the host's own, which is the only one this process can
        // honestly speak for.
        if (OwnerClientId == NetworkManager.ServerClientId)
        {
            ownedMask.Value = StoreService.OwnedMask;
        }
    }
}
