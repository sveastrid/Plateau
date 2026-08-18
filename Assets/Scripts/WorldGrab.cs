using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Both grips: move, turn and resize everything in the room except the players.
///
/// The players are not under World Root, so "except the players" needs no filter — it falls out of
/// the hierarchy. Only the content root's pose is computed and sent; nothing is ever reparented,
/// which is what the version of this in the project's ancestor did and paid for with a per-child
/// transform readback and accumulated float error.
///
/// Runs after BoardAnchor (order 10) and RoomContent (order 15) so the hand positions it reads are
/// in this frame's aligned room frame, not last frame's.
/// </summary>
[DefaultExecutionOrder(20)]
public class WorldGrab : MonoBehaviour
{
    public InputReader inputs;
    public Transform leftHand;
    public Transform rightHand;
    public GrabControl leftGrabber;
    public GrabControl rightGrabber;

    // Hands closer together than this at the start make the span ratio wildly sensitive — a 1 cm
    // wobble becomes a 20% scale change. Below it, move and turn still work and scale is held.
    public float MinSpan = 0.08f;

    // Server round trips per second while the gesture runs. The holder drives its own view at frame
    // rate regardless; this is only what everybody else sees. 20 with RoomContent's filter is
    // smooth; the Netcode tick is 30 (OpeningScene.unity:523) so there is no point going higher.
    public float SendHz = 20f;

    /// <summary>True on any client currently running the gesture. Read by CameraController2.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>True only on the client that owns the world right now. Read by RoomContent.</summary>
    public static bool LocalHoldsWorld { get; private set; }

    enum State { Idle, Claiming, Holding }
    State state = State.Idle;

    Vector3 startCenter, startPos;
    float startSpan, startScale, startYaw;
    float lastHandYaw, yawAccum;
    bool yawPrimed;                 // false until the hand axis has been usable at least once
    bool claimSent;                 // a Claim went out and has not been matched by a Release
    float nextSendAt;
    float nextClaimAt;

    // Re-ask this often while waiting for the lock. A claim that arrives while somebody else holds
    // the world is refused silently, and if they let go in the same breath there is nothing left to
    // observe — this client would wait in Claiming for as long as it kept squeezing. Claiming again
    // is free (the RPC is a no-op unless the lock is actually available) and closes that hole.
    const float ClaimRetrySeconds = 0.5f;

    void Start()
    {
        // Serialized references are the fast path; these are the safety net. CameraController2
        // (:41-50) had to add exactly this after a field rename silently dropped every scene's
        // value — StairsGame.unity still carries the dead key from that rename.
        if (inputs == null)
        {
            GameObject reader = GameObject.Find("Input Reader");
            inputs = reader != null ? reader.GetComponent<InputReader>() : null;
        }
        if (leftHand == null)
        {
            leftHand = FindDescendant(transform, "Left Hand");
        }
        if (rightHand == null)
        {
            rightHand = FindDescendant(transform, "Right Hand");
        }
        if (leftGrabber == null)
        {
            Transform t = FindDescendant(transform, "Left Grabber");
            leftGrabber = t != null ? t.GetComponent<GrabControl>() : null;
        }
        if (rightGrabber == null)
        {
            Transform t = FindDescendant(transform, "Right Grabber");
            rightGrabber = t != null ? t.GetComponent<GrabControl>() : null;
        }

        if (inputs == null || leftHand == null || rightHand == null)
        {
            Debug.LogWarning("WorldGrab: no Input Reader or no Left/Right Hand under " + name +
                             "; the two-grip world grab is off until they are assigned.");
        }
    }

    void OnDisable()
    {
        // A game switch destroys this rig mid-gesture. Releasing here is what stops the lock from
        // leaking and freezing the board for the whole room.
        if (state != State.Idle)
        {
            Cancel();
        }
    }

    void Update()
    {
        RoomAnchor room = RoomAnchor.Instance;
        NetworkManager nm = NetworkManager.Singleton;
        if (room == null || nm == null || inputs == null || leftHand == null || rightHand == null)
        {
            return;
        }

        // Levels, not edges: InputReader's *Down flags are one-shot and this is a held gesture.
        bool both = inputs.LeftGrip && inputs.RightGrip;

        switch (state)
        {
            case State.Idle:
                if (both && CanStart(room))
                {
                    Capture(room);
                    Claim(room);
                    state = State.Claiming;
                    IsActive = true;
                }
                break;

            case State.Claiming:
                if (!both)
                {
                    Cancel();
                    break;
                }
                if (room.worldHolder.Value == nm.LocalClientId)
                {
                    // Re-capture. The claim cost a round trip and the hands moved during it; using
                    // the pre-claim baseline would make the board jump the moment the lock lands.
                    Capture(room);
                    LocalHoldsWorld = true;
                    state = State.Holding;
                }
                else if (Time.unscaledTime >= nextClaimAt)
                {
                    Claim(room);
                }
                break;

            case State.Holding:
                if (!both || room.worldHolder.Value != nm.LocalClientId)
                {
                    Commit(room);
                    Cancel();
                    break;
                }
                Drive(room);
                break;
        }
    }

    void Claim(RoomAnchor room)
    {
        room.ClaimWorldServerRpc();
        claimSent = true;
        nextClaimAt = Time.unscaledTime + ClaimRetrySeconds;
    }

    bool CanStart(RoomAnchor room)
    {
        if (room.worldHolder.Value != RoomAnchor.NoHolder)
        {
            return false;
        }
        // Two grips with a piece in one of them is a two-handed piece grab, not a world grab.
        // Nothing is tagged Grabbable today, so both of these are false — this is here so it does
        // not have to be retrofitted the day the first piece becomes grabbable.
        if (leftGrabber != null && leftGrabber.lineHit)
        {
            return false;
        }
        if (rightGrabber != null && rightGrabber.lineHit)
        {
            return false;
        }
        return true;
    }

    void Capture(RoomAnchor room)
    {
        Vector3 l = leftHand.position;
        Vector3 r = rightHand.position;

        startCenter = (l + r) * 0.5f;
        startSpan = Vector3.Distance(l, r);
        yawAccum = 0f;

        // Squeezing with the controllers held together makes the hand axis vertical and the yaw
        // undefined. Leave it unprimed rather than seeding it with a made-up 0: the first frame the
        // axis IS usable would otherwise read as a large turn and snap the board.
        yawPrimed = TryHandYaw(l, r, out lastHandYaw);

        startPos = room.contentPos.Value;
        startYaw = room.contentYaw.Value;

        // Clamped on read as well as on write. SetContentServerRpc is the only writer and it clamps,
        // so this only ever bites if the scale arrived as zero some other way — and Drive divides by
        // it.
        startScale = Mathf.Clamp(room.contentScale.Value, RoomAnchor.MinScale, RoomAnchor.MaxScale);
    }

    void Drive(RoomAnchor room)
    {
        Vector3 l = leftHand.position;
        Vector3 r = rightHand.position;
        Vector3 center = (l + r) * 0.5f;
        float span = Vector3.Distance(l, r);

        // Accumulate the turn from per-frame deltas. atan2 wraps at +-180 degrees; a single
        // (theta - theta0) would spin the board a full turn when the hands cross the seam, and
        // would cap the gesture at half a revolution.
        //
        // A frame with no usable hand axis contributes nothing and does not move lastHandYaw, so
        // passing through a vertical hand pose is a pause in the turn rather than a spin.
        if (TryHandYaw(l, r, out float handYaw))
        {
            if (yawPrimed)
            {
                yawAccum += Mathf.DeltaAngle(lastHandYaw, handYaw);
            }
            lastHandYaw = handYaw;
            yawPrimed = true;
        }

        float scale = startScale;
        if (startSpan >= MinSpan)
        {
            scale = Mathf.Clamp(startScale * (span / startSpan),
                                RoomAnchor.MinScale, RoomAnchor.MaxScale);
        }

        // The CLAMPED ratio, not the raw one. With the raw ratio, hitting the scale limit makes the
        // board slide out from under your hands while appearing to be stationary in size.
        float applied = scale / startScale;
        Quaternion turn = Quaternion.Euler(0f, yawAccum, 0f);

        // The similarity transform that maps startCenter -> center with rotation `turn` and scale
        // `applied`, applied to the content root's origin. The naive startPos + (center -
        // startCenter) does not have the property you want: it drags the board sideways whenever
        // the board's origin is not exactly under your hands, which it never is.
        Vector3 pos = center + turn * (applied * (startPos - startCenter));
        float yaw = startYaw + yawAccum;

        // Locally at frame rate, so the hands never lag. Everybody else gets it at SendHz and
        // filters it (RoomContent).
        if (RoomContent.Instance != null)
        {
            RoomContent.Instance.ApplyImmediate(pos, yaw, scale);
        }

        if (Time.unscaledTime >= nextSendAt)
        {
            nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, SendHz);
            room.SetContentServerRpc(pos, yaw, scale);
        }
    }

    void Commit(RoomAnchor room)
    {
        // One unconditional final send. Without it the room keeps whatever the last rate-limited
        // sample happened to be, which can be up to 1/SendHz of hand movement away from where the
        // person let go. Called before Cancel(), and reliable-sequenced delivery means the server
        // applies this pose before it clears the lock that authorises it.
        if (RoomContent.Instance != null)
        {
            Transform t = RoomContent.Instance.transform;
            room.SetContentServerRpc(t.position, t.eulerAngles.y, t.localScale.x);
        }
    }

    void Cancel()
    {
        RoomAnchor room = RoomAnchor.Instance;

        // Release on claimSent, NOT on LocalHoldsWorld. Letting go before the claim's round trip
        // completes is easy to do and would otherwise leave the server granting a lock nobody is
        // using, for the rest of the session. RPCs from one sender are reliable-sequenced, so a
        // Release sent after a Claim is always processed after it, and ReleaseWorldServerRpc is a
        // no-op for anyone who is not the holder.
        //
        // The connection check is for OnDisable: this also runs on application quit and on a
        // NetworkReconnectHandler shutdown, where sending anything logs a warning at best.
        NetworkManager nm = NetworkManager.Singleton;
        if (room != null && claimSent && nm != null && nm.IsConnectedClient)
        {
            room.ReleaseWorldServerRpc();
        }

        claimSent = false;
        state = State.Idle;
        IsActive = false;
        LocalHoldsWorld = false;
    }

    /// <summary>
    /// Yaw of the hand axis, floor-projected. False when the two controllers are stacked vertically,
    /// where the axis has no yaw and atan2 would amplify tracking noise into a spin.
    /// </summary>
    static bool TryHandYaw(Vector3 l, Vector3 r, out float yaw)
    {
        Vector3 axis = r - l;
        axis.y = 0f;
        if (axis.sqrMagnitude < 1e-4f)      // hands within 1 cm horizontally
        {
            yaw = 0f;
            return false;
        }
        yaw = Mathf.Atan2(axis.x, axis.z) * Mathf.Rad2Deg;
        return true;
    }

    /// <summary>
    /// The named descendant of this rig, at any depth. Transform.Find only looks one level down
    /// unless it is given the whole path, and "Camera Offset/Left Hand" is exactly the kind of
    /// hardcoded path this project keeps getting bitten by.
    /// </summary>
    private static Transform FindDescendant(Transform parent, string childName)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
            {
                return child;
            }

            Transform found = FindDescendant(child, childName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
