using UnityEngine;
using UnityEngine.UI;

public class ScrollTextWithJoystick : MonoBehaviour
{
    public InputReader inputs;
    public ScrollRect scrollRect;
    public float scrollSpeed = 2f;

    [Tooltip("The beam, so the stick only scrolls the text it is resting on. Assigned by " +
             "RulesBoard alongside inputs, the way Panel.Bind pushes it into a ScrollList.")]
    public pointerControl pointer;

    [Tooltip("Only scroll while the beam is on this object or a child of it. The rules board is " +
             "present for the whole game now, and rightJoystick.y is also Chasms' move count and " +
             "BASH's artillery aim — without this, aiming a shot scrolls the rules.")]
    public bool joystickNeedsHover = true;

    void Update()
    {
        if (inputs == null || scrollRect == null) return;

        if (joystickNeedsHover && !PointerIsOverMe()) return;

        // Right joystick only. There used to be a fallback to the left stick whenever the right one
        // was centred — and the left stick is locomotion and snap-turn (CameraController2), so for
        // a player who is not colocated, walking forward also scrolled the rules. The right stick
        // is the documented control; see Assets/Scripts/CLAUDE.md, Input.
        float scrollInput = inputs.rightJoystick.y;

        if (Mathf.Abs(scrollInput) > 0.1f)
        {
            // The ScrollRect verticalNormalizedPosition goes from 0 (bottom) to 1 (top)
            // If we push joystick up (positive), we want to read further down, which means moving the position towards 0
            float newPos = scrollRect.verticalNormalizedPosition - scrollInput * scrollSpeed * Time.deltaTime;
            scrollRect.verticalNormalizedPosition = Mathf.Clamp01(newPos);
        }
    }

    /// <summary>
    /// Transform.IsChildOf is true for the transform itself, and RulesBoard puts this component on
    /// the same object as the body's own "RulesBody" key — so resting the beam anywhere on the
    /// rules text is the hover.
    /// </summary>
    private bool PointerIsOverMe()
    {
        if (pointer == null || pointer.currentKey == null)
        {
            return false;
        }

        return pointer.currentKey.transform.IsChildOf(transform);
    }
}
