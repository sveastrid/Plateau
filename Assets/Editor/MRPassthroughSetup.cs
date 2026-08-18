using UnityEditor;
using UnityEngine;

/// <summary>
/// Finishes the Meta-side half of the passthrough conversion.
///
/// Everything else in the MR conversion lives in files that can be edited directly
/// (scenes, URP assets, ProjectSettings, AndroidManifest.xml). OVRProjectConfig cannot —
/// it is a ScriptableObject the Meta XR SDK creates on demand inside the package folder,
/// so it has to be written from the Editor. Without it:
///
///   * Meta > Tools > Android Manifest Tool regenerates AndroidManifest.xml WITHOUT the
///     passthrough feature tag, silently undoing Assets/Plugins/Android/AndroidManifest.xml.
///   * Meta's Project Setup Tool reports a Required failure for MR apps that still use the
///     black system splash background.
///   * The same tool strips the two anchor permissions colocation needs, so every anchor call
///     fails with Failure_SpacePermissionInsufficient and nobody shares a room frame.
///   * It also strips com.oculus.permission.BOUNDARY_VISIBILITY, and without that the runtime
///     refuses PassthroughController's request to hide the Guardian — players get the blue grid
///     the moment they walk past their drawn boundary.
///
/// Runs once automatically, and is re-runnable from the menu.
/// </summary>
[InitializeOnLoad]
public static class MRPassthroughSetup
{
    // Bumped to .v2 when anchor support was added below, and to .v3 when boundary visibility
    // was, so the pass re-runs on machines whose SessionState still says an earlier pass is done.
    const string AppliedKey = "MRTemplate.MRPassthroughSetup.Applied.v3";

    static MRPassthroughSetup()
    {
        if (SessionState.GetBool(AppliedKey, false)) return;
        SessionState.SetBool(AppliedKey, true);

        // Defer: OVRProjectConfig touches the AssetDatabase, which is not safe from a
        // static constructor during domain reload.
        EditorApplication.delayCall += () => Apply(false);
    }

    [MenuItem("MR Template/MR/Configure Meta Passthrough Project Config")]
    static void ApplyFromMenu() => Apply(true);

    static void Apply(bool verbose)
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (config == null)
        {
            Debug.LogWarning("MRPassthroughSetup: could not load OVRProjectConfig. Is the Meta XR Core SDK installed?");
            return;
        }

        bool changed = false;

        // Required, not Supported: this build is MR-only, matching
        // android:required="true" on com.oculus.feature.PASSTHROUGH in the manifest.
        if (config.insightPassthroughSupport != OVRProjectConfig.FeatureSupport.Required)
        {
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            changed = true;
        }

        // Colocation. anchorSupport enabled adds com.oculus.permission.USE_ANCHOR_API to the
        // manifest, sharedAnchorSupport non-None adds IMPORT_EXPORT_IOT_MAP_DATA
        // (OVRManifestPreprocessor.cs:765-783). Both are also written by hand into
        // Assets/Plugins/Android/AndroidManifest.xml; these two lines are what stops Meta's
        // Manifest Tool from taking them straight back out again.
        if (config.anchorSupport != OVRProjectConfig.AnchorSupport.Enabled)
        {
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            changed = true;
        }

        // Supported, not Required: a headset that cannot share anchors should still install and
        // run, as a player in a different room.
        if (config.sharedAnchorSupport != OVRProjectConfig.FeatureSupport.Supported)
        {
            config.sharedAnchorSupport = OVRProjectConfig.FeatureSupport.Supported;
            changed = true;
        }

        // Boundary suppression, so players can walk the real room without redrawing their Guardian.
        // Supported, not Required: a headset on an OS too old for the API should still install and
        // run, it just keeps its Guardian. This is also what makes OVRManifestPreprocessor.cs:1033
        // emit com.oculus.permission.BOUNDARY_VISIBILITY, and what stops Meta's Manifest Tool
        // taking that permission back out of Assets/Plugins/Android/AndroidManifest.xml.
        if (config.boundaryVisibilitySupport != OVRProjectConfig.FeatureSupport.Supported)
        {
            config.boundaryVisibilitySupport = OVRProjectConfig.FeatureSupport.Supported;
            changed = true;
        }

        // Meta treats a black system splash as a Required failure for MR apps — it flashes
        // an opaque black frame before the room fades in.
        if (config.systemLoadingScreenBackground != OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough)
        {
            config.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
            changed = true;
        }

        // Quest 2 passthrough is greyscale and low resolution; Quest Pro / 3 / 3S are colour.
        // Quest 1 has no useful passthrough and is dropped.
        var wanted = new[]
        {
            OVRProjectConfig.DeviceType.Quest2,
            OVRProjectConfig.DeviceType.QuestPro,
            OVRProjectConfig.DeviceType.Quest3,
            OVRProjectConfig.DeviceType.Quest3S,
        };

        foreach (var device in wanted)
        {
            if (!config.targetDeviceTypes.Contains(device))
            {
                config.targetDeviceTypes.Add(device);
                changed = true;
            }
        }

        if (config.targetDeviceTypes.Remove(OVRProjectConfig.DeviceType.Quest))
        {
            changed = true;
        }

        if (changed)
        {
            OVRProjectConfig.CommitProjectConfig(config);
            Debug.Log("MRPassthroughSetup: OVRProjectConfig updated — passthrough Required, " +
                      "anchors + anchor sharing on, boundary visibility Supported, " +
                      "contextual passthrough splash, Quest 2/Pro/3/3S targets.");
        }
        else if (verbose)
        {
            Debug.Log("MRPassthroughSetup: OVRProjectConfig already configured for passthrough.");
        }
    }
}
