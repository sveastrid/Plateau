using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Editor.Configuration;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Keeps DefaultNetworkPrefabs.asset able to agree between an Editor and a device build.
///
/// NetworkConfig.ForceSamePrefabs is 1 (OpeningScene.unity), which folds every registered network
/// prefab's GlobalObjectIdHash into the config hash a joining client sends and the server compares.
/// Any difference in the SET of registered prefabs is a hard refusal, and Netcode disconnects with
/// no reason string — so it surfaces only as a join that hangs on "Joining room...".
///
/// The list used to hold eight Meta XR SDK Building Blocks prefabs that Netcode's own asset
/// post-processor put there, four of them inside the package's Editor/ folder. An asset under an
/// Editor/ folder is not included in a player build, so the Editor registered twelve hashes and a
/// Quest build registered eight, and Editor-to-device could never connect no matter how many times
/// it was rebuilt. Nothing in this project spawns any of them — colocation here is hand-rolled on
/// BoardAnchor, not on Meta's blocks — so they were pruned. See docs/bigFixes1.md §1.
///
/// Two jobs, because pruning alone does not hold:
///
///   1. Turn Netcode's auto-generator off, so a package re-resolve does not re-add them. CLAUDE.md
///      notes re-resolves happen often enough here to need a patch script re-run.
///   2. Fail the BUILD if a foreign entry ever gets back in, rather than shipping an APK that
///      cannot talk to the Editor and finding out in a headset.
///
/// (2) is the one that actually protects: it cannot be defeated by a setting silently reverting.
/// </summary>
[InitializeOnLoad]
public static class NetworkPrefabListGuard
{
    const string AppliedKey = "MRBoardGame2.NetworkPrefabListGuard.Applied.v1";
    const string SettingsMenuPath = "Edit > Project Settings > Netcode for GameObjects";

    static NetworkPrefabListGuard()
    {
        if (SessionState.GetBool(AppliedKey, false))
        {
            return;
        }
        SessionState.SetBool(AppliedKey, true);

        // Defer: this touches the AssetDatabase, which is not safe from a static constructor
        // during a domain reload. Same reasoning as MRPassthroughSetup.
        EditorApplication.delayCall += () => Apply(false);
    }

    [MenuItem("MR Template/MR/Check Network Prefab List")]
    static void ApplyFromMenu() => Apply(true);

    static void Apply(bool verbose)
    {
        DisableAutoGeneration(verbose);
        ReportForeignEntries(verbose);
    }

    // ------------------------------------------------------------------ the generator

    static void DisableAutoGeneration(bool verbose)
    {
        NetcodeForGameObjectsProjectSettings settings = NetcodeForGameObjectsProjectSettings.instance;
        if (settings == null)
        {
            return;
        }

        if (!settings.GenerateDefaultNetworkPrefabs)
        {
            if (verbose)
            {
                Debug.Log("NetworkPrefabListGuard: the default network prefabs generator is already off.");
            }
            return;
        }

        settings.GenerateDefaultNetworkPrefabs = false;

        // SaveSettings() is internal to Unity.Netcode.Editor, so there is no supported call for it
        // from this assembly. Reflection persists it to ProjectSettings/NetcodeForGameObjects.asset;
        // if that ever stops working the in-memory value above still holds for this session, and the
        // build guard below is what actually keeps a bad list out of an APK.
        MethodInfo save = typeof(NetcodeForGameObjectsProjectSettings)
            .GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic);

        if (save != null)
        {
            save.Invoke(settings, null);
            Debug.Log("NetworkPrefabListGuard: turned OFF \"Generate Default Network Prefabs List\". " +
                      "ProjectSettings/NetcodeForGameObjects.asset is not gitignored — COMMIT IT, or " +
                      "the next clone is back to a list that re-adds package prefabs.");
        }
        else
        {
            Debug.LogWarning("NetworkPrefabListGuard: could not persist the setting. Untick it by " +
                             "hand under " + SettingsMenuPath + " and commit " +
                             "ProjectSettings/NetcodeForGameObjects.asset.");
        }
    }

    // ------------------------------------------------------------------ the list

    static void ReportForeignEntries(bool verbose)
    {
        NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(
            "Assets/DefaultNetworkPrefabs.asset");

        if (list == null)
        {
            return;
        }

        string bad = FindForeignEntry(list);
        if (bad != null)
        {
            Debug.LogWarning("NetworkPrefabListGuard: DefaultNetworkPrefabs.asset holds an entry " +
                             "that will not exist in a player build: " + bad +
                             ". Remove it, or Editor-to-device connections will be refused.");
        }
        else if (verbose)
        {
            Debug.Log("NetworkPrefabListGuard: DefaultNetworkPrefabs.asset holds " +
                      list.PrefabList.Count + " project prefabs and nothing foreign.");
        }
    }

    /// <summary>
    /// The description of a list entry that an Editor and a player build would disagree about, or
    /// null when every entry is safe. Anything outside Assets/, or under any Editor/ folder, is
    /// present in the Editor and absent in the player.
    /// </summary>
    internal static string FindForeignEntry(NetworkPrefabsList list)
    {
        foreach (NetworkPrefab entry in list.PrefabList)
        {
            if (entry == null)
            {
                continue;
            }

            if (entry.Prefab == null)
            {
                // A null reference is what an Editor-only prefab already looks like from a build,
                // and NetworkPrefab.Validate() would silently drop it.
                return "a missing prefab reference";
            }

            string path = AssetDatabase.GetAssetPath(entry.Prefab);
            if (!path.StartsWith("Assets/") || path.Contains("/Editor/"))
            {
                return path;
            }
        }

        return null;
    }
}

/// <summary>
/// The backstop. Fails the build rather than shipping a prefab list the Editor and the device
/// cannot agree on — see NetworkPrefabListGuard for why that matters.
/// </summary>
public class NetworkPrefabListCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(
            "Assets/DefaultNetworkPrefabs.asset");

        if (list == null)
        {
            return;
        }

        string bad = NetworkPrefabListGuard.FindForeignEntry(list);
        if (bad != null)
        {
            throw new BuildFailedException(
                "DefaultNetworkPrefabs.asset contains an Editor-only or package prefab: " + bad +
                ". With NetworkConfig.ForceSamePrefabs on, this build could never connect to the " +
                "Editor. Remove the entry and turn off \"Generate Default Network Prefabs List\" " +
                "under Edit > Project Settings > Netcode for GameObjects.");
        }
    }
}
