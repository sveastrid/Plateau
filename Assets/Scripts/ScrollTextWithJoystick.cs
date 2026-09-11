using UnityEngine;
using UnityEngine.UI;

public class ScrollTextWithJoystick : MonoBehaviour
{
    public InputReader inputs;
    public ScrollRect scrollRect;
    public float scrollSpeed = 2f;

    void Update()
    {
        if (inputs == null || scrollRect == null) return;

        // Right joystick only. There used to be a fallback to the left stick whenever the right one
        // was centred — and the left stick is locomotion and snap-turn (CameraController2), so for
        // a player who is not colocated, walking forward with the room menu open also scrolled the
        // rules. The right stick is the documented control; see Assets/Scripts/CLAUDE.md, Input.
        float scrollInput = inputs.rightJoystick.y;

        if (Mathf.Abs(scrollInput) > 0.1f)
        {
            // The ScrollRect verticalNormalizedPosition goes from 0 (bottom) to 1 (top)
            // If we push joystick up (positive), we want to read further down, which means moving the position towards 0
            float newPos = scrollRect.verticalNormalizedPosition - scrollInput * scrollSpeed * Time.deltaTime;
            scrollRect.verticalNormalizedPosition = Mathf.Clamp01(newPos);
        }
    }
}
