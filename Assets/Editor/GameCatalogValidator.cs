using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// The check GameModule.cs has claimed exists since it was written: "GameCatalogValidator complains
/// if you forget the second step". It did not exist. This is it.
///
/// Every fault it catches is one that is otherwise invisible until a headset:
///
///  - a module that is not in the catalog gets no menu key and no route, silently;
///  - a sceneName missing from EditorBuildSettings fails inside LoadScene with InvalidSceneName,
///    visible only as a Debug.LogError from GameSelector while three people wait;
///  - a duplicate or reused libraryBit hands a player a game they did not buy, because the bit is
///    what travels on the wire;
///  - a duplicate productId makes two games share one library entry;
///  - a paid game with no SKU has no way to be bought and no error that says so.
///
/// Structured like NetworkPrefabListGuard: a warning on domain load so it is noticed while working,
/// and a build failure so it cannot be shipped past.
/// </summary>
[InitializeOnLoad]
public static class GameCatalogValidator
{
    const string AppliedKey = "MRBoardGame2.GameCatalogValidator.Applied.v1";

    static GameCatalogValidator()
    {
        if (SessionState.GetBool(AppliedKey, false))
        {
            return;
        }
        SessionState.SetBool(AppliedKey, true);

        // Deferred for the reason NetworkPrefabListGuard defers: this touches the AssetDatabase,
        // which is not safe from a static constructor during a domain reload.
        EditorApplication.delayCall += () => Report(false);
    }

    [MenuItem("MR Template/MR/Check Game Catalog")]
    static void CheckFromMenu() => Report(true);

    static void Report(bool verbose)
    {
        List<string> faults = Validate();

        if (faults.Count > 0)
        {
            Debug.LogWarning("GameCatalogValidator: " + faults.Count + " problem(s) with the game " +
                             "catalog. A build will fail until they are fixed.\n  " +
                             string.Join("\n  ", faults));
        }
        else if (verbose)
        {
            Debug.Log("GameCatalogValidator: the catalog is consistent.");
        }
    }

    /// <summary>Every fault found, most specific first. Empty means the catalog is shippable.</summary>
    internal static List<string> Validate()
    {
        List<string> faults = new List<string>();

        GameCatalog catalog = AssetDatabase.LoadAssetAtPath<GameCatalog>(
            "Assets/Resources/GameCatalog.asset");

        if (catalog == null)
        {
            faults.Add("There is no Assets/Resources/GameCatalog.asset, so no game can be reached " +
                       "from the menu at all.");
            return faults;
        }

        // --- every module asset in the project is in the catalog
        HashSet<GameModule> inCatalog = new HashSet<GameModule>();
        for (int i = 0; i < catalog.games.Count; i++)
        {
            if (catalog.games[i] != null)
            {
                inCatalog.Add(catalog.games[i]);
            }
        }

        foreach (string guid in AssetDatabase.FindAssets("t:GameModule"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameModule module = AssetDatabase.LoadAssetAtPath<GameModule>(path);
            if (module != null && !inCatalog.Contains(module))
            {
                faults.Add(path + " is a GameModule that is not in GameCatalog.asset, so nothing " +
                           "can reach it. Add it to the catalog's Games list.");
            }
        }

        if (catalog.defaultGame == null)
        {
            faults.Add("GameCatalog.asset has no Default Game, so the host has nowhere to open the room.");
        }
        else if (!inCatalog.Contains(catalog.defaultGame))
        {
            faults.Add("GameCatalog.asset's Default Game is not in its own Games list.");
        }

        // --- the build scene list, which is the failure that only shows up in a headset
        HashSet<string> buildScenes = new HashSet<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (!scene.enabled)
            {
                continue;
            }
            buildScenes.Add(System.IO.Path.GetFileNameWithoutExtension(scene.path));
        }

        // --- per-row checks
        Dictionary<string, string> productIds = new Dictionary<string, string>();
        Dictionary<int, string> libraryBits = new Dictionary<int, string>();
        Dictionary<string, string> gameKeys = new Dictionary<string, string>();

        for (int i = 0; i < catalog.games.Count; i++)
        {
            GameModule module = catalog.games[i];
            if (module == null)
            {
                faults.Add("GameCatalog row " + i + " is empty.");
                continue;
            }

            string who = module.name;

            if (string.IsNullOrEmpty(module.gameKey))
            {
                faults.Add(who + " has no gameKey, so it gets no menu key.");
            }
            else if (gameKeys.TryGetValue(module.gameKey, out string otherKey))
            {
                faults.Add(who + " and " + otherKey + " share the gameKey '" + module.gameKey +
                           "'. The menu dispatches on that string.");
            }
            else
            {
                gameKeys[module.gameKey] = who;
            }

            if (string.IsNullOrEmpty(module.sceneName))
            {
                faults.Add(who + " has no sceneName.");
            }
            else if (!buildScenes.Contains(module.sceneName))
            {
                faults.Add(who + "'s scene '" + module.sceneName + "' is not an enabled scene in " +
                           "File > Build Settings. LoadScene will fail with InvalidSceneName, and " +
                           "the only sign of it is a Debug.LogError from GameSelector at runtime.");
            }

            if (string.IsNullOrEmpty(module.productId))
            {
                faults.Add(who + " has no productId. It is what the saved library is keyed by and " +
                           "it must be stable forever.");
            }
            else if (productIds.TryGetValue(module.productId, out string otherProduct))
            {
                faults.Add(who + " and " + otherProduct + " share the productId '" +
                           module.productId + "', so they share one library entry.");
            }
            else
            {
                productIds[module.productId] = who;
            }

            if (module.libraryBit < 0 || module.libraryBit > 63)
            {
                faults.Add(who + " has libraryBit " + module.libraryBit + ", which is outside 0..63. " +
                           "The library travels as a ulong mask.");
            }
            else if (libraryBits.TryGetValue(module.libraryBit, out string otherBit))
            {
                faults.Add(who + " and " + otherBit + " share libraryBit " + module.libraryBit +
                           ". Owning one would grant the other.");
            }
            else
            {
                libraryBits[module.libraryBit] = who;
            }

            if (module.isPaid && string.IsNullOrEmpty(module.metaSku))
            {
                faults.Add(who + " is marked paid but has no metaSku, so there is nothing for " +
                           "IAP.LaunchCheckoutFlow to buy.");
            }

            if (module.maxPlayers > 12)
            {
                faults.Add(who + " allows " + module.maxPlayers + " players. The Relay allocation " +
                           "and PlayerRing are both 12.");
            }
        }

        return faults;
    }
}

/// <summary>
/// The backstop. Fails the build rather than shipping a catalog whose faults only appear in a
/// headset, which is the same bargain NetworkPrefabListCheck makes.
/// </summary>
public class GameCatalogCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        List<string> faults = GameCatalogValidator.Validate();
        if (faults.Count > 0)
        {
            throw new BuildFailedException(
                "The game catalog has " + faults.Count + " problem(s):\n  " +
                string.Join("\n  ", faults));
        }
    }
}
