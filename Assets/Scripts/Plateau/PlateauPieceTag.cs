using TMPro;
using UnityEngine;

/// <summary>
/// A spawned piece visual and what it stands for. Added by <see cref="PlateauPieceView"/>, never
/// wired in the Editor.
///
/// These objects are NOT NetworkObjects. They are a pure function of PlateauGame's replicated
/// lists, rebuilt whenever those change, which is why nothing here is serialized.
/// </summary>
[DisallowMultipleComponent]
public class PlateauPieceTag : MonoBehaviour
{
    public byte plateau;
    public byte seat;
    public byte kind;
    public byte count;

    /// <summary>
    /// For a bridge that has been laid across a gap, the edge it spans; PlateauConst.NoIndex for
    /// everything else, including an unplaced bridge sitting in reserve on a plateau.
    /// </summary>
    public byte edge = PlateauConst.NoIndex;

    public bool IsPlacedBridge => edge != PlateauConst.NoIndex;

    // Resolved once at spawn, by name, the way PlayerControls resolves its avatar parts.
    [System.NonSerialized] public Transform countTransform;
    [System.NonSerialized] public TextMeshPro countLabel;
    [System.NonSerialized] public Renderer disc;
    [System.NonSerialized] public PlateauTint tint;

    // Layout target, in the piece container's local space. Positions are smoothed toward this so
    // that a stack leaving a plateau slides its neighbours over rather than popping them.
    [System.NonSerialized] public Vector3 targetLocalPosition;
    [System.NonSerialized] public Vector3 targetScale = Vector3.one;
    /// <summary>The prefab's authored root scale — the size a piece is on the central plateau.</summary>
    [System.NonSerialized] public float prefabScale = 1f;
    /// <summary>The Count child's authored local scale, before the billboard's compensation.</summary>
    [System.NonSerialized] public Vector3 countBaseScale = Vector3.one;

    /// <summary>False until the first frame this piece has been placed. A piece that has just
    /// appeared snaps; it must not glide in from wherever Instantiate put it.</summary>
    [System.NonSerialized] public bool primed;
}
