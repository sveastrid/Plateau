using UnityEngine;

/// <summary>
/// Enables Meta Insight Passthrough and switches the camera to composite over it.
/// Attach to "XRRig". Keeps a reference to the old skybox so VR mode is still reachable.
///
/// The on/off state is static on purpose: it survives the OpeningScene -> GameScene load,
/// so a user who turns passthrough off in the lobby stays in VR when the room loads.
///
/// Also owns Guardian suppression, because Meta requires the two to move together — see
/// SetPassthrough. Suppression additionally needs boundaryVisibilitySupport in OVRProjectConfig
/// (Assets/Editor/MRPassthroughSetup.cs) and com.oculus.permission.BOUNDARY_VISIBILITY in the
/// manifest; without both, the runtime silently refuses and the boundary stays visible.
/// </summary>
public class PassthroughController : MonoBehaviour
{
    public OVRPassthroughLayer passthroughLayer;  // Underlay layer on the rig
    public Camera targetCamera;                   // "Main Camera"
    public Material vrSkybox;                     // Assets/Materials/Space.mat
    public bool startInPassthrough = true;
    public bool suppressBoundary = true;          // hide the Guardian while passthrough is on

    static bool passthroughOn;
    static bool stateRemembered;

    void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;

        EnsureOvrComponents();

        // Honour a choice the user already made this session; otherwise use the scene default.
        SetPassthrough(stateRemembered ? passthroughOn : startInPassthrough);
    }

    /// <summary>
    /// Passthrough is a compositor layer owned by the Meta runtime, not a render feature —
    /// without an OVRManager and an underlay OVRPassthroughLayer the background is simply
    /// black, with no error. Wiring them in the scene is preferred (Meta's Project Setup Tool
    /// only validates components it can find in the scene), but provision them here as well so
    /// a rig rebuilt without them still shows the room instead of failing silently.
    /// </summary>
    void EnsureOvrComponents()
    {
        if (OVRManager.instance == null &&
            FindFirstObjectByType<OVRManager>(FindObjectsInactive.Include) == null)
        {
            Debug.LogWarning("PassthroughController: no OVRManager in the scene, adding one to " + name + ".");
            gameObject.AddComponent<OVRManager>();
        }

        if (passthroughLayer == null)
        {
            passthroughLayer = FindFirstObjectByType<OVRPassthroughLayer>(FindObjectsInactive.Include);
        }

        if (passthroughLayer == null)
        {
            Debug.LogWarning("PassthroughController: no OVRPassthroughLayer in the scene, adding one to " + name + ".");
            passthroughLayer = gameObject.AddComponent<OVRPassthroughLayer>();
            passthroughLayer.projectionSurfaceType = OVRPassthroughLayer.ProjectionSurfaceType.Reconstructed;
        }

        // Overlay would draw the camera feed on top of the maths. Underlay puts it behind.
        passthroughLayer.overlayType = OVROverlay.OverlayType.Underlay;
    }

    public void SetPassthrough(bool on)
    {
        passthroughOn = on;
        stateRemembered = true;

        if (OVRManager.instance != null)
        {
            OVRManager.instance.isInsightPassthroughEnabled = on;

            // Suppression has to track the passthrough layer, not just be switched on once: in
            // full-VR mode the player cannot see the real room, so the Guardian is the only thing
            // keeping them off the furniture and it must come back. OVRManager re-requests this
            // every frame until the runtime agrees (OVRManager.UpdateBoundary), so setting it here
            // is enough even though passthrough has not finished initialising yet.
            OVRManager.instance.shouldBoundaryVisibilityBeSuppressed = on && suppressBoundary;
        }

        if (passthroughLayer != null)
            passthroughLayer.enabled = on;

        if (targetCamera == null) targetCamera = Camera.main;

        if (targetCamera != null)
        {
            // Solid transparent black lets the compositor show the camera feed underneath.
            targetCamera.clearFlags = on ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
            targetCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        }

        RenderSettings.skybox = on ? null : vrSkybox;
    }

    public void Toggle() => SetPassthrough(!passthroughOn);
    public static bool IsPassthroughOn() => passthroughOn;
}
