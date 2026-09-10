using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// One pressable key. The pointer's trigger capsule reports the <see cref="keyName"/> of whatever
/// collider tagged "key" it is touching; every menu in the project dispatches on that string and
/// never on a child index.
///
/// It draws itself two ways, because there are two kinds of key here now: a 3D key swaps a
/// <see cref="Material"/> on its MeshRenderer, and a Canvas row tints a <see cref="Graphic"/>.
/// Both go through <see cref="Apply"/>, so "on" and "off" cannot come to mean different things in
/// the two renderers. A key uses whichever of the two it actually has, and having neither is legal
/// — a key with no visible highlight still presses.
/// </summary>
public class keyInfo : MonoBehaviour
{
    public string keyName = "1";
    public Material currentMaterial;
    public Material offMaterial;
    public Material onMaterial;

    /// <summary>
    /// Widened from TextMeshPro to TMP_Text so a Canvas row's TextMeshProUGUI fits the same slot.
    /// Both derive from TMP_Text and both have SetText, and a serialized reference survives the
    /// widening because the stored value is a PPtr to an object that is still assignable.
    /// </summary>
    public TMP_Text keyLabel;

    public bool alwaysOn = false;
    public bool overrideNameChange = false;

    [Header("Canvas rows")]
    [Tooltip("Filled automatically from this object's own Graphic when left empty. A 3D key has " +
             "none and tints nothing.")]
    public Graphic targetGraphic;
    public Color offColor = new Color(0.13f, 0.15f, 0.19f, 0.92f);
    public Color onColor = new Color(0.16f, 0.42f, 0.62f, 0.98f);

    // Cached rather than fetched per call. keyInfo is on every row of a scrolling list now, and
    // OnTriggerStay drives ChangeToOnMaterial for as long as the beam rests on one.
    private MeshRenderer meshRenderer;
    private bool alreadyBig = false;

    void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        if (targetGraphic == null)
        {
            targetGraphic = GetComponent<Graphic>();
        }
    }

    void Start()
    {
        // Deliberately does NOT push offMaterial at the renderer: the 3D prefabs are already
        // authored with it, and GetComponent<MeshRenderer>().material is the *instantiating*
        // accessor, so touching it here would leak one material per key for no visible change.
        currentMaterial = offMaterial;

        if (targetGraphic != null && !alwaysOn)
        {
            targetGraphic.color = offColor;
        }

        if (keyLabel != null && !overrideNameChange)
        {
            keyLabel.SetText(keyName);
        }
    }

    /// <summary>
    /// MakeBigger multiplies localScale, which on a RectTransform is fine only as long as nothing
    /// else writes the scale. Canvas rows are therefore positioned by anchoredPosition and must
    /// never sit under a LayoutGroup — see ScrollList, which lays its slots out by hand for exactly
    /// this reason.
    /// </summary>
    public void MakeBigger()
    {
        if (!alreadyBig)
        {
            this.transform.localScale *= 1.2f;
            alreadyBig = true;
        }
    }

    public void MakeSmaller()
    {
        if (alreadyBig)
        {
            this.transform.localScale /= 1.2f;
            alreadyBig = false;
        }
    }

    public void ChangeToOnMaterial()
    {
        if (!alwaysOn)
        {
            Apply(true);
        }
    }

    public void ChangeToOffMaterial()
    {
        if (!alwaysOn)
        {
            Apply(false);
        }
    }

    public void KeepOn()
    {
        alwaysOn = true;
        Apply(true);
    }

    public void TurnOff()
    {
        alwaysOn = false;
        Apply(false);
    }

    /// <summary>The one place that decides what "on" and "off" look like, in either renderer.</summary>
    private void Apply(bool on)
    {
        currentMaterial = on ? onMaterial : offMaterial;

        if (meshRenderer != null && currentMaterial != null)
        {
            meshRenderer.material = currentMaterial;
        }

        if (targetGraphic != null)
        {
            targetGraphic.color = on ? onColor : offColor;
        }
    }
}
