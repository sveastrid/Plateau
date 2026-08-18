using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Temporary instrumentation. You cannot tell colocation working from lucky, and this is what tells
/// them apart. Lives on XRRig and prints through Debug.Log, which DebugLog mirrors into the
/// in-headset box on the Debugger object — so no new UI and no adb logcat.
///
/// Read it like this:
///
///   recenters ticks and the cones go wrong at the same moment -> no shared frame. Baseline this
///     BEFORE BoardAnchor lands.
///   recenters ticks and nothing moves                          -> alignment is working. This is
///     the acceptance test.
///   tracked=False for more than a moment  -> that client is not really in the room, or the room is
///     badly mapped.
///   uuid differs between two headsets     -> they are on different anchors.
///   head.y about 1.36, or about 2.7, on a standing adult -> Camera Offset was never zeroed. Every
///     other number is meaningless until this clears.
///   two headsets on one table read different rig.y -> the floor-calibration disagreement
///     CameraController2.MaxAnchorHeightDisagreement exists to absorb. Measure it before and after.
///   scale differs between two headsets while nobody is gesturing -> RoomContent is not reading
///     RoomAnchor, or the world-grab lock leaked.
///
/// Delete this component when the numbers are known: the debug box holds ten lines and this logs
/// forever.
/// </summary>
public class ColocationProbe : MonoBehaviour
{
    public float IntervalSeconds = 1f;

    /// <summary>
    /// Every system-initiated recenter of the tracking origin. OVRDisplay polls
    /// GetLocalTrackingSpaceRecenterCount() every frame on Quest and raises RecenteredPose
    /// (OVRDisplay.cs:150-159), so this catches the ones the app never asked for — exactly the ones
    /// that slide a one-shot alignment out of the shared frame.
    /// </summary>
    private int recenters;

    private float nextPrintAt;
    private Transform head;

    void OnEnable()
    {
        // OVRManager.display is null in the Editor with no headset. Null-guard both ends.
        if (OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose += OnRecentered;
        }
    }

    void OnDisable()
    {
        if (OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose -= OnRecentered;
        }
    }

    private void OnRecentered()
    {
        recenters++;
    }

    void Update()
    {
        if (Time.realtimeSinceStartup < nextPrintAt)
        {
            return;
        }
        nextPrintAt = Time.realtimeSinceStartup + Mathf.Max(0.25f, IntervalSeconds);

        if (head == null && Camera.main != null)
        {
            head = Camera.main.transform;
        }

        StringBuilder line = new StringBuilder(160);
        line.Append("probe rig.y=").Append(transform.position.y.ToString("F2"));
        line.Append(" head.y=").Append(head != null ? head.position.y.ToString("F2") : "-");
        line.Append(" aligned=").Append(CameraController2.LocalIsAligned);

        BoardAnchor anchor = BoardAnchor.Instance;
        line.Append(" | anchored=").Append(anchor != null && anchor.HasAnchor);
        line.Append(" tracked=").Append(anchor != null && anchor.AnchorIsTracked);
        line.Append(" uuid=").Append(Short(anchor != null ? anchor.BoundUuid : ""));
        line.Append(" recenters=").Append(recenters);

        RoomAnchor room = RoomAnchor.Instance;
        if (room != null)
        {
            line.Append(" | scale=").Append(room.contentScale.Value.ToString("F2"));
            line.Append(" holder=").Append(room.worldHolder.Value == RoomAnchor.NoHolder
                                               ? "none"
                                               : room.worldHolder.Value.ToString());
        }

        AppendRemotePlayers(line);

        Debug.Log(line.ToString());
    }

    private static void AppendRemotePlayers(StringBuilder line)
    {
        // FindObjectsByType rather than ConnectedClientsList: that list is only populated on the
        // server, and the probe has to read the same on a client.
        PlayerControls[] players = FindObjectsByType<PlayerControls>();

        foreach (PlayerControls player in players)
        {
            if (player == null || player.IsOwner)
            {
                continue;
            }

            line.Append(" | ").Append(player.playerName.Value.ToString());
            line.Append(" head.y=").Append(player.facePos.Value.y.ToString("F2"));
            line.Append(" rH.y=").Append(player.rHPos.Value.y.ToString("F2"));
        }
    }

    private static string Short(string uuid)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            return "-";
        }
        return uuid.Length <= 8 ? uuid : uuid.Substring(0, 8);
    }
}
