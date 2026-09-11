using System;
using UnityEngine;
using TMPro;

/// <summary>
/// A world-space Canvas panel with a header, a status line and any number of
/// <see cref="ScrollList"/>s, plus the one press model this project has always used: pressed on
/// trigger **down**, acted on trigger **up**, so sliding off a key cancels it.
///
/// That model lived only in MenuControl and in GameController's keyboard loop, copied by hand.
/// It lives here now, so the lobby's two panels, the code pad and the room menu cannot drift apart
/// on what a press means.
///
/// Unit convention: the Canvas is at localScale 0.001, so **1 UI unit = 1 mm**. A 1.2 m x 0.8 m
/// panel is sizeDelta (1200, 800) and a 96-unit row is 9.6 cm tall. The project previously had two
/// conventions in play at once (Text Input at 0.01, the rules panel at 0.002), which is how panels
/// end up subtly different sizes.
/// </summary>
public class Panel : MonoBehaviour
{
    public TMP_Text header;
    public TMP_Text status;

    [Tooltip("The prose block under the list — a game's blurb and rules. Hidden when empty.")]
    public TMP_Text detail;

    [Tooltip("Every list on this panel. Their scroll arrows are consumed before KeyPressed fires.")]
    public ScrollList[] lists = new ScrollList[0];

    /// <summary>Set by whoever opened the panel. Both are on PersistentRig and outlive scenes.</summary>
    public InputReader inputs;
    public pointerControl pointer;

    /// <summary>
    /// The keyName of a row or key that was pressed and released on this panel. Scroll arrows never
    /// reach here — the list that owns them has already acted.
    /// </summary>
    public event Action<string> KeyPressed;

    private keyInfo pressedKey;

    /// <summary>Wire the panel to the rig and push the references down into every list.</summary>
    public void Bind(InputReader reader, pointerControl beam)
    {
        inputs = reader;
        pointer = beam;

        for (int i = 0; i < lists.Length; i++)
        {
            if (lists[i] != null)
            {
                lists[i].inputs = reader;
                // The beam too, so a list can tell whether the joystick is meant for it. Two lists
                // on this panel and the rules wing beside it all read the same axis.
                lists[i].pointer = beam;
            }
        }
    }

    public void SetHeader(string text)
    {
        if (header != null)
        {
            header.SetText(text ?? "");
        }
    }

    /// <summary>
    /// The panel's one message line. Empty hides it, so a panel with nothing to say does not carry
    /// a blank strip. This is where a join failure, a purchase failure or "Lobby is unreachable"
    /// goes — the lobby used to nudge a TextMeshPro transform by a relative 7 cm to make room for
    /// one, which had to be undone exactly once or the text walked off.
    /// </summary>
    public void SetStatus(string text)
    {
        if (status == null)
        {
            return;
        }

        status.SetText(text ?? "");
        status.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    /// <summary>
    /// The prose block. It occupies the same band as the second list — there is no room on a
    /// 1200 x 900 canvas for both, and no surface has ever wanted both: the lobby's Library panel
    /// uses the detail block and no action list, the room menu the reverse. Say so out loud if one
    /// ever does, rather than silently drawing them on top of each other.
    /// </summary>
    public void SetDetail(string text)
    {
        if (detail == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(text) && lists.Length > 1 && lists[1] != null &&
            lists[1].Rows.Count > 0)
        {
            Debug.LogWarning("Panel " + name + ": the detail block and list 1 both have content " +
                             "and they share the same rect, so one is drawn over the other. Give " +
                             "the panel more height, or move one of them.");
        }

        detail.SetText(text ?? "");
        detail.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    /// <summary>The list at this index, or null. 0 is the main list, 1 the action list.</summary>
    public ScrollList List(int index)
    {
        return index >= 0 && index < lists.Length ? lists[index] : null;
    }

    void Update()
    {
        if (inputs == null || pointer == null)
        {
            return;
        }

        if (inputs.RightMainTriggerDown)
        {
            if (pointer.currentKey != null && IsMine(pointer.currentKey.transform))
            {
                pressedKey = pointer.currentKey;
                pressedKey.MakeBigger();
                Press(pressedKey, true);
            }
            return;
        }

        if (inputs.RightMainTriggerUp && pressedKey != null)
        {
            pressedKey.MakeSmaller();
            Press(pressedKey, false);

            // Read the name off the object that was pressed, not off whatever the beam is touching
            // now: the trigger may have been released somewhere else entirely.
            string keyName = pressedKey.keyName;
            pressedKey = null;

            // Still over it? MenuControl's rule — sliding off a key cancels it.
            if (pointer.currentKey == null || pointer.currentKey.keyName != keyName)
            {
                return;
            }

            for (int i = 0; i < lists.Length; i++)
            {
                if (lists[i] != null && lists[i].HandleKey(keyName))
                {
                    return;
                }
            }

            KeyPressed?.Invoke(keyName);
        }
    }

    /// <summary>
    /// Two panels are open at once in the lobby and they share one pointer, so each must only act
    /// on its own keys or a press on the Library would also fire on Play.
    /// </summary>
    private bool IsMine(Transform t)
    {
        return t != null && t.IsChildOf(transform);
    }

    /// <summary>
    /// Pressed-down feedback for a full-width Canvas row, which cannot use keyInfo.MakeBigger:
    /// growing a 1160-unit row by 20% pushes 116 units off each side into the viewport mask and
    /// 19 units down over the next row, which reads as a glitch rather than as feedback. A 3D key
    /// still grows — keyInfo.growOnPress is what decides, per key.
    /// </summary>
    private static void Press(keyInfo key, bool down)
    {
        PanelRow row = key != null ? key.GetComponent<PanelRow>() : null;
        if (row != null)
        {
            row.ShowPressed(down);
        }
    }

    void OnDisable()
    {
        if (pressedKey != null)
        {
            pressedKey.MakeSmaller();
            Press(pressedKey, false);
            pressedKey = null;
        }
    }
}
