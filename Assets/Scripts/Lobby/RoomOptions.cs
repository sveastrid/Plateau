using UnityEngine;

/// <summary>
/// What the host chose on the Play panel, carried the few frames from "press Host" to
/// RoomAnchor.OnNetworkSpawn.
///
/// It has to be a static, the way GameController.joinCode and nickName already are: RoomAnchor is
/// spawned by BoardAnchor.HandleServerStarted, which runs off NetworkManager.OnServerStarted, and
/// there is no object alive at that moment that both the panel and the anchor can see.
///
/// **Reset it explicitly.** Static state outlives a scene, and a Play session with domain reload
/// off — so a public room hosted once would make every later private room in the same session
/// public. BoardAnchor.Awake already resets CameraController2.LocalIsAligned for precisely this
/// reason and now resets this beside it.
/// </summary>
public static class RoomOptions
{
    /// <summary>A public room is listed in the directory and is locked to one game.</summary>
    public static bool IsPublic;

    /// <summary>The game a public room is locked to. Empty in a private room.</summary>
    public static string GameKey = "";

    /// <summary>What the directory shows as the room's name. Empty falls back to the host's name.</summary>
    public static string RoomName = "";

    public static void Reset()
    {
        IsPublic = false;
        GameKey = "";
        RoomName = "";
    }

    public static void SetPrivate()
    {
        Reset();
    }

    public static void SetPublic(string gameKey, string roomName)
    {
        IsPublic = true;
        GameKey = gameKey ?? "";
        RoomName = roomName ?? "";

        if (string.IsNullOrEmpty(GameKey))
        {
            Debug.LogWarning("RoomOptions: a public room with no game key cannot be locked to a " +
                             "game, so it will behave as private once the room opens.");
            IsPublic = false;
        }
    }
}
