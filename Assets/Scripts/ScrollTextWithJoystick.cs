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

        // Use the right joystick Y axis to scroll
        float scrollInput = inputs.rightJoystick.y;

        // Fallback to left joystick if needed
        if (Mathf.Abs(scrollInput) < 0.1f)
        {
            scrollInput = inputs.leftJoystick.y;
        }

        if (Mathf.Abs(scrollInput) > 0.1f)
        {
            // The ScrollRect verticalNormalizedPosition goes from 0 (bottom) to 1 (top)
            // If we push joystick up (positive), we want to read further down, which means moving the position towards 0
            float newPos = scrollRect.verticalNormalizedPosition - scrollInput * scrollSpeed * Time.deltaTime;
            scrollRect.verticalNormalizedPosition = Mathf.Clamp01(newPos);
        }
    }
}
