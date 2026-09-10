using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// A Canvas-drawn pressable row: uGUI for the pixels, a collider for the press.
///
/// The project has no EventSystem-driven input module and deliberately does not want one — a
/// ray-driven uGUI module is ~250 lines that nothing else here would use. Everything pressable
/// already has a working path: pointerControl's trigger capsule reports the keyInfo.keyName of any
/// collider tagged "key". So a row carries an Image for the look and a BoxCollider + kinematic
/// Rigidbody + tag "key" + keyInfo for the press, which is the same widget the room menu has
/// always cloned.
///
/// The collider is sized in UI units (the row is ~1180 x 90 of them); the Canvas' 0.001 scale
/// converts to metres, so 1 UI unit = 1 mm everywhere in this toolkit.
/// </summary>
public class PanelRow : MonoBehaviour
{
    public keyInfo key;
    public Image background;
    public Image thumbnail;
    public TMP_Text title;
    public TMP_Text subtitle;
    public TMP_Text state;

    [Header("Greyed rows")]
    public Color titleColor = new Color(0.94f, 0.96f, 1f, 1f);
    public Color disabledTitleColor = new Color(0.55f, 0.58f, 0.63f, 1f);
    public Color selectedColor = new Color(0.18f, 0.50f, 0.36f, 0.98f);

    private BoxCollider box;
    private Color authoredOffColor;
    private bool authoredCaptured;

    /// <summary>The data this slot is currently showing, or null while the slot is empty.</summary>
    public RowData Data { get; private set; }

    void Awake()
    {
        if (key == null)
        {
            key = GetComponent<keyInfo>();
        }
        if (background == null)
        {
            background = GetComponent<Image>();
        }
        box = GetComponent<BoxCollider>();
        CaptureAuthored();
    }

    private void CaptureAuthored()
    {
        if (!authoredCaptured && key != null)
        {
            authoredOffColor = key.offColor;
            authoredCaptured = true;
        }
    }

    /// <summary>
    /// Point this slot at a row of data. Cheap enough to call every frame the list scrolls, which
    /// is what recycling costs instead of instantiating.
    ///
    /// overrideNameChange is set for the same reason MenuControl sets it on a cloned key:
    /// keyInfo.Start() rewrites the label with the keyName, and on a row bound in the frame it was
    /// instantiated that has not run yet, so a title differing from the id would be silently
    /// overwritten a moment later.
    /// </summary>
    public void Bind(RowData row)
    {
        CaptureAuthored();
        Data = row;

        if (row == null)
        {
            gameObject.SetActive(false);
            return;
        }

        gameObject.SetActive(true);

        if (key != null)
        {
            key.keyName = row.id;
            key.overrideNameChange = true;
            key.keyLabel = title;
            // A selected row stays lit; keyInfo.KeepOn/TurnOff own alwaysOn, and the hover tint
            // rides on top of offColor, so the "current choice" look is a different resting colour
            // rather than a second highlight state that could fight the beam's.
            key.offColor = row.selected ? selectedColor : authoredOffColor;
            if (background != null)
            {
                background.color = key.offColor;
            }
        }

        if (title != null)
        {
            title.SetText(row.title ?? "");
            title.color = row.pressable ? titleColor : disabledTitleColor;
        }

        if (subtitle != null)
        {
            subtitle.SetText(row.subtitle ?? "");
            subtitle.gameObject.SetActive(!string.IsNullOrEmpty(row.subtitle));
        }

        if (state != null)
        {
            state.SetText(row.state ?? "");
            state.color = row.pressable ? titleColor : disabledTitleColor;
        }

        if (thumbnail != null)
        {
            thumbnail.sprite = row.thumbnail;
            thumbnail.enabled = row.thumbnail != null;
        }

        // An unpressable row loses its collider rather than keeping it and refusing later. The beam
        // then does not stop on it and the trigger does nothing, which reads as "not available"
        // without any code having to explain itself.
        if (box != null)
        {
            box.enabled = row.pressable;
        }
    }

    /// <summary>Hide the slot. Used for the tail of a list shorter than its slot count.</summary>
    public void Clear()
    {
        Bind(null);
    }
}
