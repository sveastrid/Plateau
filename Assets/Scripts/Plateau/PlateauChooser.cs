using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// "Plateau Chooser" (ChasmGame.unity, scene root, currently a sibling of "Plateau Controller") --
/// a novelty prop, not one of the 41 real plateaus PlateauBoard tracks. Click it the same way a
/// plateau piece is selected (PlateauSelection's point-and-click-from-a-distance idiom -- NOT
/// pointerControl's physical-touch one, which is unrelated and untouched by this feature) and it
/// shuffles through the three per-plateau material tiers (15/35/50.mat -- the same materials 40 of
/// the 41 real plateaus carry as a per-instance override; see docs/plateauRules.md, "the colors
/// represent the percent chance of that color being chosen"), slowing down over about two seconds
/// before landing on one, weighted 15% / 35% / 50%. Independently, its Chasmfiend child has a 30%
/// chance of waking up.
///
/// All of that is authoritative on PlateauGame (chooserSpinEpoch / chooserResultEpoch /
/// chooserMaterialIndex / chooserChasmfiendActive), exactly like every other piece of board state.
/// This script only asks for a spin (RequestSpinChooserServerRpc) and plays a local,
/// latency-tolerant flourish while waiting for the real answer. A plain MonoBehaviour, not a
/// NetworkBehaviour: it has no state of its own worth replicating and needs no NetworkObject, the
/// same as PlateauSelection/PlateauPieceView/PlateauSpawnMenu, all of which talk to
/// PlateauGame.Instance instead of carrying their own wire state.
///
/// Material swap, not PlateauTint: unlike the 41 real plateaus (up to 36 owner-coloured pieces on
/// each, via a shared tinted material -- see PlateauTint's own class doc for why a swap would be
/// wrong there), this is one unique object, so keyInfo.cs's
/// GetComponent&lt;MeshRenderer&gt;().material = ... idiom is the right one here.
///
/// Order 25, tied with PlateauSelection: both consume the hit PointerBeam (24) produced this same
/// frame, and neither depends on the other.
/// </summary>
[DefaultExecutionOrder(25)]
public class PlateauChooser : MonoBehaviour
{
    [Header("Scene (resolved by name if empty, and re-resolved after a game switch)")]
    public InputReader inputs;
    public MenuControl menu;
    public PointerBeam beam;

    [Header("Result materials (indexed the same way PlateauGame.chooserMaterialIndex is)")]
    [Tooltip("chooserMaterialIndex 0 -- 15% chance.")]
    public Material material15;
    [Tooltip("chooserMaterialIndex 1 -- 35% chance.")]
    public Material material35;
    [Tooltip("chooserMaterialIndex 2 -- 50% chance. Matches this object's currently authored material.")]
    public Material material50;

    [Header("Chasmfiend child (starts inactive in the scene)")]
    public GameObject chasmfiendObject;

    [Header("Flourish timing (local-only cosmetic; not synced to the server's own delay)")]
    [Tooltip("Seconds the shuffle spends accelerating-to-decelerating before it either lands (if " +
             "the server has already resolved) or keeps looping slowly while it waits.")]
    public float FlourishDuration = 2f;
    [Tooltip("Seconds between material swaps at the start of the shuffle.")]
    public float FastInterval = 0.05f;
    [Tooltip("Seconds between material swaps at the end of the shuffle, and while it loops waiting " +
             "on latency.")]
    public float SlowInterval = 0.35f;

    const int MaterialCount = 3;

    MeshRenderer meshRenderer;
    PlateauGame subscribed;
    Coroutine flourish;
    bool pressed;
    bool spinning;

    void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
    }

    void Start()
    {
        if (meshRenderer == null)
        {
            Debug.LogError("PlateauChooser: no MeshRenderer on " + name + ".");
        }
        if (material15 == null || material35 == null || material50 == null)
        {
            Debug.LogError("PlateauChooser: one or more result materials is not assigned on " + name + ".");
        }
        if (chasmfiendObject == null)
        {
            Debug.LogError("PlateauChooser: chasmfiendObject is not assigned on " + name + ".");
        }
    }

    void OnEnable()
    {
        SceneManager.activeSceneChanged += HandleSceneChanged;
    }

    void OnDisable()
    {
        SceneManager.activeSceneChanged -= HandleSceneChanged;
        Unsubscribe();
        StopFlourish();
    }

    void HandleSceneChanged(Scene from, Scene to)
    {
        inputs = null;
        menu = null;
        beam = null;
        pressed = false;
    }

    void Unsubscribe()
    {
        if (subscribed == null)
        {
            return;
        }
        subscribed.chooserSpinEpoch.OnValueChanged -= HandleSpinEpochChanged;
        subscribed.chooserResultEpoch.OnValueChanged -= HandleResultEpochChanged;
        subscribed = null;
    }

    void Update()
    {
        PlateauGame game = PlateauGame.Instance;
        if (game != subscribed)
        {
            Unsubscribe();
            subscribed = game;
            if (subscribed != null)
            {
                subscribed.chooserSpinEpoch.OnValueChanged += HandleSpinEpochChanged;
                subscribed.chooserResultEpoch.OnValueChanged += HandleResultEpochChanged;

                // A NetworkVariable's initial synced value raises no OnValueChanged (the same reason
                // PlateauGame.OnNetworkSpawn fires BoardChanged once by hand after subscribing) --
                // snap to whatever is already there with no flourish, exactly like a freshly spawned
                // piece "primes" instead of gliding in from nowhere.
                ApplyFinalState();
            }
        }

        if (!Bind() || !Playable())
        {
            pressed = false;
            return;
        }

        bool hitThis = beam.HasHit && beam.Hit.collider != null &&
                       beam.Hit.collider.GetComponentInParent<PlateauChooser>() == this;

        if (inputs.RightMainTriggerDown)
        {
            pressed = hitThis;
        }
        else if (inputs.RightMainTriggerUp)
        {
            // Press must end on what it started on -- sliding off cancels, same as PlateauSelection.
            // !spinning is a courtesy only (avoids a pointless RPC); the server's own
            // chooserSpinPending guard is what actually matters for correctness.
            if (pressed && hitThis && !spinning)
            {
                subscribed.RequestSpinChooserServerRpc();
            }
            pressed = false;
        }
    }

    /// <summary>
    /// Mirrors PlateauSelection.Playable(), minus what does not apply here: PlateauBoard's bake
    /// state is irrelevant (this is not one of its 41 tracked tiles), and there is no spawnMenu
    /// exception -- that exists only so PlateauSelection does not drop a multi-step piece selection
    /// while the wrist menu borrows the trigger, and a Chooser click has no selection to preserve.
    /// LeftGrip alone (which the spawn menu already requires to open) is enough on its own.
    /// </summary>
    bool Playable()
    {
        if (subscribed == null || !subscribed.IsSpawned || !subscribed.boardLive.Value)
        {
            return false;
        }

        if (PlateauGame.LocalSeat() < 0)
        {
            return false;                       // the server has not seated this player yet
        }

        if (menu != null && menu.IsOpen)
        {
            return false;
        }

        if (WorldGrab.IsActive)
        {
            return false;
        }

        if (inputs.LeftGrip || inputs.RightGrip)
        {
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ flourish

    void HandleSpinEpochChanged(int oldValue, int newValue) => BeginFlourish();
    void HandleResultEpochChanged(int oldValue, int newValue) => ApplyFinalState();

    void BeginFlourish()
    {
        StopFlourish();
        flourish = StartCoroutine(FlourishRoutine());
    }

    void StopFlourish()
    {
        if (flourish != null)
        {
            StopCoroutine(flourish);
            flourish = null;
        }
        spinning = false;
    }

    /// <summary>Snap straight to the server's current answer, no shuffle -- used both when a spin
    /// actually resolves and when this script first binds to a PlateauGame that already has one.</summary>
    void ApplyFinalState()
    {
        StopFlourish();
        if (subscribed == null)
        {
            return;
        }
        ApplyMaterial(subscribed.chooserMaterialIndex.Value);
        if (chasmfiendObject != null)
        {
            chasmfiendObject.SetActive(subscribed.chooserChasmfiendActive.Value);
        }
    }

    /// <summary>
    /// Rapid shuffle easing into a slow one over FlourishDuration, then keeps looping at the slow
    /// interval until HandleResultEpochChanged cuts it off with the server's real answer.
    /// Deliberately does not try to land at exactly the server's ~2 second mark -- latency means
    /// this client's clock and the server's are never exactly aligned, and a flourish that is still
    /// visibly "thinking" reads better than one that freezes early on the wrong guess.
    /// </summary>
    IEnumerator FlourishRoutine()
    {
        spinning = true;
        int last = -1;
        float elapsed = 0f;

        while (elapsed < FlourishDuration)
        {
            last = RandomOtherIndex(last);
            ApplyMaterial(last);

            float t = Mathf.Clamp01(elapsed / FlourishDuration);
            float interval = Mathf.Lerp(FastInterval, SlowInterval, t * t);
            yield return new WaitForSeconds(interval);
            elapsed += interval;
        }

        while (true)
        {
            last = RandomOtherIndex(last);
            ApplyMaterial(last);
            yield return new WaitForSeconds(SlowInterval);
        }
    }

    /// <summary>A different index than last time, so every shuffle tick visibly changes something.</summary>
    static int RandomOtherIndex(int exclude)
    {
        if (exclude < 0)
        {
            return Random.Range(0, MaterialCount);
        }
        int roll = Random.Range(0, MaterialCount - 1);
        return roll >= exclude ? roll + 1 : roll;
    }

    void ApplyMaterial(int index)
    {
        if (meshRenderer == null)
        {
            return;
        }
        Material m = MaterialForIndex(index);
        if (m != null)
        {
            meshRenderer.material = m;      // keyInfo.cs's instancing-accessor swap -- fine for one object
        }
    }

    Material MaterialForIndex(int index)
    {
        switch (index)
        {
            case 0: return material15;
            case 1: return material35;
            case 2: return material50;
            default: return null;
        }
    }

    // ------------------------------------------------------------------ binding

    /// <summary>Re-resolve everything this scene owns -- the same contract PlateauSelection.Bind()
    /// and PlateauSpawnMenu.Bind() follow after a LoadSceneMode.Single game switch.</summary>
    bool Bind()
    {
        if (inputs == null)
        {
            GameObject go = GameObject.Find("Input Reader");
            inputs = go != null ? go.GetComponent<InputReader>() : null;
        }

        if (menu == null)
        {
            GameObject go = GameObject.Find("Menu Manager");
            menu = go != null ? go.GetComponent<MenuControl>() : null;
        }

        if (beam == null)
        {
            GameObject rig = GameObject.Find("XRRig");
            Transform pointer = rig != null ? HierarchyUtils.FindDescendant(rig.transform, "Pointer") : null;
            beam = pointer != null ? pointer.GetComponent<PointerBeam>() : null;
        }

        return inputs != null && beam != null;
    }
}
