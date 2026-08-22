using UnityEngine;

/// <summary>
/// Marks a Plateau object with its index in <see cref="PlateauBoard"/>.
///
/// Added by PlateauBoard at runtime, never wired in the Editor, so the index cannot drift from the
/// one the bake produced. A raycast lands on a grandchild — Plateau.prefab's collider is on the
/// nested model, not the root — so readers must use GetComponentInParent, not GetComponent.
/// </summary>
[DisallowMultipleComponent]
public class PlateauTag : MonoBehaviour
{
    public int index = -1;
}
