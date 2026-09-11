using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// A scrolling list of <see cref="PanelRow"/>s, built as N fixed slots that never leave the
/// viewport, with the scroll offset changing which data each slot shows.
///
/// The recycling is not an optimisation, it is what makes the colliders safe. A RectMask2D clips
/// *pixels* and not colliders, so the naive "tall content, slide the content" list leaves rows you
/// cannot see sitting in front of you where the beam can still press them. A list whose rows never
/// move outside the viewport has nothing off-screen to press.
///
/// Scrolling is the ScrollTextWithJoystick idiom — right joystick Y — with a repeat delay, so one
/// flick moves one row rather than twelve.
/// </summary>
public class ScrollList : MonoBehaviour
{
    /// <summary>keyNames of the generated arrows. Dispatched like any other key.</summary>
    public const string ScrollUpKey = "ScrollUp";
    public const string ScrollDownKey = "ScrollDown";

    public RectTransform viewport;

    [Tooltip("Inactive in the prefab and cloned once per visible slot — MenuControl's idiom. " +
             "Cloning something already correct in this hierarchy keeps the collider, rigidbody, " +
             "colours and label scale out of the code.")]
    public PanelRow rowTemplate;

    [Tooltip("Authored, inactive. Shown only when there is more data than slots. A list whose row " +
             "count can exceed visibleRows MUST have all three: without them the extra rows are " +
             "reachable only by a joystick push with nothing on screen to suggest it.")]
    public GameObject scrollUp;
    public GameObject scrollDown;
    public TMP_Text counter;

    [Tooltip("Optional heading above the list. Hidden when empty, so a panel that uses one list " +
             "does not carry a blank strip where the other one's caption would be.")]
    public TMP_Text caption;

    [Tooltip("How many rows are drawn at once. The list allocates this many slots and no more.")]
    public int visibleRows = 6;

    [Tooltip("Row pitch in UI units. 1 UI unit = 1 mm, so 84 is an 8.4 cm row — legible at 1.6 m. " +
             "The row template is 76, and the 8-unit difference is the gutter between rows: slots " +
             "are positioned by pitch, so a row shorter than the pitch gets its gap for free.")]
    public float rowHeight = 84f;

    public InputReader inputs;

    [Tooltip("The beam, so the joystick only scrolls the list it is resting on. Pushed down by " +
             "Panel.Bind alongside inputs.")]
    public pointerControl pointer;

    [Tooltip("Only scroll on the joystick while the beam is inside this list's viewport. Two " +
             "lists and the rules panel all read rightJoystick.y, so without this one push moves " +
             "every one of them at once.")]
    public bool joystickNeedsHover = true;

    [Tooltip("Joystick repeat: the pause before the second row, then the pause between the rest.")]
    public float firstRepeatDelay = 0.25f;
    public float repeatDelay = 0.08f;

    [Tooltip("Below this the joystick is treated as centred.")]
    public float deadZone = 0.5f;

    private readonly List<RowData> rows = new List<RowData>();
    private PanelRow[] slots;
    private int offset;
    private float nextRepeat;
    private int lastDirection;

    /// <summary>Rows currently bound, in data order. Read-only to callers.</summary>
    public IReadOnlyList<RowData> Rows => rows;

    void Awake()
    {
        EnsureSlots();
    }

    private void EnsureSlots()
    {
        if (slots != null)
        {
            return;
        }

        if (rowTemplate == null || viewport == null)
        {
            Debug.LogError("ScrollList on " + name + ": rowTemplate or viewport is not assigned, " +
                           "so the list can never draw anything.");
            slots = new PanelRow[0];
            return;
        }

        int count = Mathf.Max(1, visibleRows);
        slots = new PanelRow[count];

        for (int i = 0; i < count; i++)
        {
            GameObject clone = Instantiate(rowTemplate.gameObject, viewport);
            clone.name = "Row " + i;
            clone.SetActive(true);

            RectTransform rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                // Positioned by hand, never by a LayoutGroup: keyInfo.MakeBigger multiplies
                // localScale on press, and a layout group would fight it every frame.
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(1, 1);
                rt.pivot = new Vector2(0.5f, 1);
                rt.anchoredPosition = new Vector2(0, -i * rowHeight);
            }

            // The press target, not the pixels. The row's BoxCollider is authored against one
            // panel's viewport width and does not track the RectTransform, so a panel of any other
            // width would get a target that does not match the row it is drawn on.
            BoxCollider box = clone.GetComponent<BoxCollider>();
            if (box != null && rt != null)
            {
                float width = viewport.rect.width;
                float height = rt.rect.height;
                box.size = new Vector3(width, height, box.size.z);
                box.center = new Vector3(0f, -height * 0.5f, box.center.z);
            }

            slots[i] = clone.GetComponent<PanelRow>();
            if (slots[i] != null)
            {
                slots[i].Clear();
            }
        }
    }

    /// <summary>
    /// The heading over this list. Empty hides it — the lobby's panels use one list and would
    /// otherwise carry a blank strip where the room menu's second caption goes.
    /// </summary>
    public void SetCaption(string text)
    {
        if (caption == null)
        {
            return;
        }

        caption.SetText(text ?? "");
        caption.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    /// <summary>
    /// Replace the list's contents. The offset is clamped rather than reset, so a panel that
    /// refreshes itself (a lobby query, a purchase landing) does not throw the player back to the
    /// top of a list they had scrolled.
    /// </summary>
    public void SetData(IReadOnlyList<RowData> data)
    {
        EnsureSlots();

        rows.Clear();
        if (data != null)
        {
            for (int i = 0; i < data.Count; i++)
            {
                if (data[i] != null)
                {
                    rows.Add(data[i]);
                }
            }
        }

        ClampOffset();
        Rebind();
    }

    /// <summary>Move by whole rows. Clamped; there is no wrap.</summary>
    public void Scroll(int delta)
    {
        int before = offset;
        offset += delta;
        ClampOffset();

        if (offset != before)
        {
            Rebind();
        }
    }

    /// <summary>
    /// True when the key belonged to this list's own arrows and has been acted on. Panels call
    /// this first and fall through to their own dispatch when it returns false.
    /// </summary>
    public bool HandleKey(string keyName)
    {
        if (keyName == ScrollUpKey)
        {
            Scroll(-1);
            return true;
        }
        if (keyName == ScrollDownKey)
        {
            Scroll(1);
            return true;
        }
        return false;
    }

    /// <summary>The data behind a pressed row, or null when the key was not one of ours.</summary>
    public RowData RowFor(string id)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].id == id)
            {
                return rows[i];
            }
        }
        return null;
    }

    void Update()
    {
        if (inputs == null || slots == null || slots.Length == 0)
        {
            return;
        }

        // Refuse to scroll while the trigger is held. Without this a press begun on row 3 and
        // released after a scroll acts on whatever row 3 now shows: the key object is the same, its
        // keyName is not. One `if` removes the whole class of bug.
        if (inputs.RightMainTrigger)
        {
            lastDirection = 0;
            return;
        }

        if (rows.Count <= slots.Length)
        {
            lastDirection = 0;
            return;
        }

        // The arrows are the affordance; the joystick is the shortcut, and it only drives the list
        // the beam is resting on. Both lists on a panel and the rules wing beside it all read
        // rightJoystick.y, so an unscoped push moved three things at once.
        if (joystickNeedsHover && !PointerIsOverMe())
        {
            lastDirection = 0;
            return;
        }

        float y = inputs.rightJoystick.y;
        int direction = y > deadZone ? -1 : (y < -deadZone ? 1 : 0);

        if (direction == 0)
        {
            lastDirection = 0;
            return;
        }

        if (direction != lastDirection)
        {
            lastDirection = direction;
            nextRepeat = Time.unscaledTime + firstRepeatDelay;
            Scroll(direction);
            return;
        }

        if (Time.unscaledTime >= nextRepeat)
        {
            nextRepeat = Time.unscaledTime + repeatDelay;
            Scroll(direction);
        }
    }

    /// <summary>
    /// Is the beam resting on a key that belongs to this list? Its own arrows count — they sit
    /// outside the viewport, and a player holding the beam on the down arrow should be able to
    /// flick the stick rather than press it repeatedly.
    /// </summary>
    private bool PointerIsOverMe()
    {
        if (pointer == null || pointer.currentKey == null)
        {
            return false;
        }

        return pointer.currentKey.transform.IsChildOf(transform);
    }

    private void ClampOffset()
    {
        int slotCount = slots != null ? slots.Length : 0;
        int max = Mathf.Max(0, rows.Count - slotCount);
        offset = Mathf.Clamp(offset, 0, max);
    }

    private void Rebind()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null)
            {
                continue;
            }

            int index = offset + i;
            slots[i].Bind(index < rows.Count ? rows[index] : null);
        }

        bool needsArrows = rows.Count > slots.Length;

        if (scrollUp != null)
        {
            scrollUp.SetActive(needsArrows);
        }
        if (scrollDown != null)
        {
            scrollDown.SetActive(needsArrows);
        }
        if (counter != null)
        {
            counter.gameObject.SetActive(needsArrows);
            if (needsArrows)
            {
                counter.SetText((offset + 1) + "-" + Mathf.Min(offset + slots.Length, rows.Count) +
                                " / " + rows.Count);
            }
        }
    }
}
