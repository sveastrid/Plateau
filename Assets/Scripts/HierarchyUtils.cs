using UnityEngine;

/// <summary>
/// Small hierarchy helpers shared by everything that resolves objects by name.
///
/// This exists because there were four copies of <see cref="FindDescendant"/> — on
/// CameraController2, WorldGrab, PlateauBoard and MenuControl — and BASH's ControlListener was
/// calling *Plateau's*, which is a game reaching into another game for a utility. The assembly
/// split turned that into a compile error, which is what it always should have been.
/// </summary>
public static class HierarchyUtils
{
    /// <summary>
    /// The named descendant at any depth, or null.
    ///
    /// Transform.Find only looks one level down unless given a whole path, and a hardcoded path
    /// like "Camera Offset/Left Hand" is exactly what this project keeps getting bitten by — it
    /// compiles fine and fails at runtime the moment somebody reorganises a prefab. Searching by
    /// name at any depth survives that; renaming the object does not, which is why the load-bearing
    /// names are listed in CLAUDE.md.
    ///
    /// Does **not** match <paramref name="parent"/> itself, only its descendants.
    /// </summary>
    public static Transform FindDescendant(Transform parent, string childName)
    {
        if (parent == null)
        {
            return null;
        }

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
