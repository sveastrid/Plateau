using UnityEngine;
#if PLATFORM_ANDROID
using UnityEngine.Android;
#endif

public class MicPermissions : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        #if PLATFORM_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);
        }
        #endif
    }
}
