using UnityEngine;

/// <summary>
/// Knobs for <see cref="MockEntitlementService"/>. At Assets/Resources/MockStoreSettings.asset so
/// the mock can find it with no serialized reference — nothing in the lobby scene owns the store.
///
/// The forced-failure and forced-cancel toggles are the point of this asset. A purchase that
/// succeeds is the easy path and it is not the one a store reviewer finds; a purchase that fails
/// halfway, or that the player backs out of, is. Without a way to force them they cannot be tested
/// at all before a real SKU exists.
/// </summary>
[CreateAssetMenu(fileName = "MockStoreSettings", menuName = "MR Board Game/Mock Store Settings")]
public class MockStoreSettings : ScriptableObject
{
    public const string ResourceName = "MockStoreSettings";

    [Tooltip("Seconds the fake checkout takes, so the 'in flight' row state is visible.")]
    public float simulatedLatencySeconds = 0.8f;

    [Tooltip("Every purchase fails. Exercises the Retry row state and the failure message.")]
    public bool forceFailure = false;

    [Tooltip("Every purchase reports Cancelled. Must NOT show as a failure to the player.")]
    public bool forceCancel = false;

    [Tooltip("Wipe the saved library on the next Initialize. Clears itself afterwards.")]
    public bool clearLibraryOnNextRun = false;

    static MockStoreSettings s_instance;
    static bool s_reported;

    /// <summary>The asset, or a throwaway default. A missing asset is not an error.</summary>
    public static MockStoreSettings Instance
    {
        get
        {
            if (s_instance != null)
            {
                return s_instance;
            }

            s_instance = Resources.Load<MockStoreSettings>(ResourceName);

            if (s_instance == null)
            {
                if (!s_reported)
                {
                    s_reported = true;
                    Debug.Log("MockStoreSettings: no Resources/" + ResourceName + ".asset, so the " +
                              "mock store runs on its defaults. Create one with Assets > Create > " +
                              "MR Board Game > Mock Store Settings to force failures.");
                }
                s_instance = CreateInstance<MockStoreSettings>();
            }

            return s_instance;
        }
    }
}
