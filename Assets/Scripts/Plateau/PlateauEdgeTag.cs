using UnityEngine;

/// <summary>
/// Marks a Bridge Spot with the edge index PlateauBoard resolved it to.
///
/// Added by PlateauBoard at runtime, never wired in the Editor, so the index cannot drift from the
/// one the bake produced -- the same contract as PlateauTag. Sits on the Bridge Spot instance root;
/// the collider that is actually raycast against lives on the "Cylinder" child (Bridge Spots.prefab
/// carries no scripts of its own), so readers must use GetComponentInParent, not GetComponent.
/// </summary>
[DisallowMultipleComponent]
public class PlateauEdgeTag : MonoBehaviour
{
    public int edge = -1;
}
