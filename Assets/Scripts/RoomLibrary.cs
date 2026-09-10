using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// What the room as a whole can play: the OR of every spawned <see cref="PlayerLibrary"/>.
///
/// The union rather than the intersection is a design choice, not an oversight. Somebody who owns
/// a game can show it to the table; the alternative — everybody must own it — makes buying a game
/// pointless until your whole group has, which is the opposite of what a store wants.
///
/// Computed **on demand**, when the menu opens and when a game switch is validated. Both are rare.
/// A cached value would need invalidating on spawn, despawn and change, which is three places to
/// forget in exchange for saving a loop over twelve objects.
/// </summary>
public static class RoomLibrary
{
    /// <summary>
    /// Every game any player in the room owns. An empty room — or one where nothing has spawned
    /// yet — reads as the local player's own library rather than as nothing, so a host alone in a
    /// freshly opened room can still see their own games in the menu.
    /// </summary>
    public static ulong Union()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            return StoreService.OwnedMask;
        }

        ulong mask = 0UL;
        bool sawAny = false;

        IReadOnlyList<NetworkClient> clients = nm.IsServer
                                                   ? (IReadOnlyList<NetworkClient>)nm.ConnectedClientsList
                                                   : null;

        if (clients != null)
        {
            for (int i = 0; i < clients.Count; i++)
            {
                NetworkObject player = clients[i] != null ? clients[i].PlayerObject : null;
                PlayerLibrary library = player != null ? player.GetComponent<PlayerLibrary>() : null;
                if (library != null)
                {
                    mask |= library.ownedMask.Value;
                    sawAny = true;
                }
            }
        }
        else
        {
            // A client cannot enumerate ConnectedClientsList, so it walks the spawned player
            // objects it does have. This is only ever used to draw the menu; the enforcement that
            // matters is GameSelector's, and that runs on the server where the list is complete.
            foreach (PlayerLibrary library in Object.FindObjectsByType<PlayerLibrary>(
                         FindObjectsSortMode.None))
            {
                mask |= library.ownedMask.Value;
                sawAny = true;
            }
        }

        return sawAny ? mask : StoreService.OwnedMask;
    }

    /// <summary>Every catalog game the room may switch into, in catalog order.</summary>
    public static List<GameModule> Playable()
    {
        List<GameModule> playable = new List<GameModule>();

        GameCatalog catalog = GameCatalog.Instance;
        if (catalog == null)
        {
            return playable;
        }

        ulong union = Union();

        for (int i = 0; i < catalog.games.Count; i++)
        {
            GameModule module = catalog.games[i];
            if (module != null && StoreService.MaskAllows(union, module))
            {
                playable.Add(module);
            }
        }

        return playable;
    }
}
