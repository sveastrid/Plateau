using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where the games put themselves so shared code can find one without naming it.
///
/// Two lookups, because the two callers want different things and conflating them is a bug:
///
/// - <see cref="ForKey"/> is for "the room is switching to this game". It must work while the room
///   is still in a *different* scene — GameSelector calls it before LoadScene — so it cannot be
///   keyed off the active scene. PlateauGame is registered the whole time because it rides on the
///   persistent Room Anchor, which is exactly what makes this reachable from Stairs.
/// - <see cref="Active"/> is for "the game on screen now". Menu actions and avatar badges belong to
///   whatever scene is loaded, and in BashGame both BashRoot and the still-registered PlateauGame
///   are present — so "the last one to register" would be a coin toss. It resolves through the
///   catalog instead.
/// </summary>
public static class GameSessionRegistry
{
    static readonly List<IGameSession> s_sessions = new List<IGameSession>();

    public static void Register(IGameSession session)
    {
        if (session == null || s_sessions.Contains(session))
        {
            return;
        }

        s_sessions.Add(session);
    }

    public static void Unregister(IGameSession session)
    {
        s_sessions.Remove(session);
    }

    /// <summary>The registered session for a game key, or null when that game is not live.</summary>
    public static IGameSession ForKey(string gameKey)
    {
        if (string.IsNullOrEmpty(gameKey))
        {
            return null;
        }

        Prune();

        for (int i = 0; i < s_sessions.Count; i++)
        {
            if (s_sessions[i].GameKey == gameKey)
            {
                return s_sessions[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The session belonging to the scene that is loaded, or null in the lobby — and also null in a
    /// game that has no session at all, which is a perfectly ordinary state. All three games happen
    /// to register one today; a fourth need not.
    /// </summary>
    public static IGameSession Active
    {
        get
        {
            GameModule module = GameCatalog.ActiveModule;
            return module != null ? ForKey(module.gameKey) : null;
        }
    }

    /// <summary>
    /// Drop entries whose object Unity has already destroyed.
    ///
    /// This list is static, so with Enter Play Mode Options and domain reload off it survives a Play
    /// session the same way CameraController2.LocalIsAligned and GameSelector.s_SwitchInProgress do.
    /// Unregister in OnDestroy is what normally keeps it clean; this is what stops a missed one from
    /// handing out a destroyed MonoBehaviour on the next run.
    /// </summary>
    static void Prune()
    {
        for (int i = s_sessions.Count - 1; i >= 0; i--)
        {
            if (IsDead(s_sessions[i]))
            {
                s_sessions.RemoveAt(i);
            }
        }
    }

    static bool IsDead(IGameSession session)
    {
        if (session == null)
        {
            return true;
        }

        // Every implementer so far is a MonoBehaviour, and Unity's overloaded == reports a
        // destroyed one as null while the C# reference is still perfectly live. That is exactly
        // the state a missed OnDestroy leaves behind, and the only one worth checking for.
        if (session is UnityEngine.Object)
        {
            return (UnityEngine.Object)session == null;
        }

        return false;
    }
}
