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
///
/// Runs once automatically, and is re-runnable from the menu.
/// </summary>
[InitializeOnLoad]
public static class MRPassthroughSetup
{
    const string AppliedKey = "MathClassroom.MRPassthroughSetup.Applied.v1";

    static MRPassthroughSetup()
    {
        if (SessionState.GetBool(AppliedKey, false)) return;
        SessionState.SetBool(AppliedKey, true);

        // Defer: OVRProjectConfig touches the AssetDatabase, which is not safe from a
        // static constructor during domain reload.
        EditorApplication.delayCall += () => Apply(false);
    }

    [MenuItem("Math Classroom/MR/Configure Meta Passthrough Project Config")]
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
                      "contextual passthrough splash, Quest 2/Pro/3/3S targets.");
        }
        else if (verbose)
        {
            Debug.Log("MRPassthroughSetup: OVRProjectConfig already configured for passthrough.");
        }
    }
}
