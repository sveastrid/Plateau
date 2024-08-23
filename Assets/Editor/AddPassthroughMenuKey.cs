using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds a "Passthrough" key to Menu1 (teacher) and Menu2 (student).
///
/// Done from the Editor rather than by hand-editing the prefab YAML for two reasons:
/// the prefabs are 140 KB of cross-referenced fileIDs, and MenuControl resolves menu
/// widgets by hardcoded child index (GetChild(0).GetChild(9).GetChild(13) and friends).
/// Unity's prefab API keeps the references correct, and SetAsLastSibling guarantees no
/// existing index shifts — inserting anywhere else silently repoints every later index
/// with no compile error and no runtime exception.
///
/// keyInfo.Start() copies keyName onto the TMP label, so the key labels itself.
/// </summary>
public static class AddPassthroughMenuKey
{
    const string KeyName = "Passthrough";
    const string TemplateKeyName = "Spherical";

    static readonly string[] MenuPrefabs =
    {
        "Assets/Prefabs/Menu1.prefab",
        "Assets/Prefabs/Menu2.prefab",
    };

    [MenuItem("Math Classroom/MR/Add Passthrough Key To Menus")]
    static void AddToAllMenus()
    {
        foreach (var path in MenuPrefabs)
        {
            AddToMenu(path);
        }

        AssetDatabase.SaveAssets();
    }

    static void AddToMenu(string path)
    {
        var root = PrefabUtility.LoadPrefabContents(path);
        if (root == null)
        {
            Debug.LogError($"AddPassthroughMenuKey: could not load {path}");
            return;
        }

        try
        {
            var keys = root.GetComponentsInChildren<keyInfo>(true);

            if (keys.Any(k => k.keyName == KeyName))
            {
                Debug.Log($"AddPassthroughMenuKey: {path} already has a {KeyName} key, skipping.");
                return;
            }

            var template = keys.FirstOrDefault(k => k.keyName == TemplateKeyName)
                           ?? keys.FirstOrDefault(k => k.keyName != "color");

            if (template == null)
            {
                Debug.LogError($"AddPassthroughMenuKey: no template key found in {path}");
                return;
            }

            var parent = template.transform.parent;
            var copy = Object.Instantiate(template.gameObject, parent);
            copy.name = KeyName;
            copy.transform.SetAsLastSibling();
            copy.transform.localScale = template.transform.localScale;
            copy.transform.localRotation = template.transform.localRotation;
            copy.transform.localPosition = NextFreeSlot(parent, template.transform);

            var info = copy.GetComponent<keyInfo>();
            info.keyName = KeyName;
            info.alwaysOn = false;
            info.overrideNameChange = false;

            // The clone's keyLabel still points at the template's label; repoint it at
            // the copy's own child so the key names itself instead of renaming the source.
            var label = copy.GetComponentInChildren<TMPro.TextMeshPro>(true);
            if (label != null)
            {
                info.keyLabel = label;
                label.SetText(KeyName);
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"AddPassthroughMenuKey: added {KeyName} key to {path} at local position " +
                      $"{copy.transform.localPosition} (last child of '{parent.name}'). " +
                      "Nudge it in the Editor if it does not sit where you want.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    /// Place the new key one row below the lowest sibling key, keeping the template's
    /// X and Z. Row spacing is taken from the real gaps between siblings so the key lands
    /// on the menu's existing grid rather than at an arbitrary offset.
    /// </summary>
    static Vector3 NextFreeSlot(Transform parent, Transform template)
    {
        var ys = new List<float>();
        for (int i = 0; i < parent.childCount; i++)
        {
            ys.Add(parent.GetChild(i).localPosition.y);
        }

        ys.Sort();

        float spacing = 0f;
        for (int i = 1; i < ys.Count; i++)
        {
            float gap = ys[i] - ys[i - 1];
            if (gap > 0.0001f && (spacing == 0f || gap < spacing))
            {
                spacing = gap;
            }
        }

        if (spacing <= 0.0001f)
        {
            spacing = Mathf.Max(template.localScale.y * 1.4f, 0.1f);
        }

        float lowest = ys.Count > 0 ? ys[0] : template.localPosition.y;

        return new Vector3(template.localPosition.x, lowest - spacing, template.localPosition.z);
    }
}
