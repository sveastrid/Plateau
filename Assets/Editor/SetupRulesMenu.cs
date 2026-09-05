using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;

[InitializeOnLoad]
public class SetupRulesMenu
{
    static SetupRulesMenu()
    {
        EditorApplication.update += RunOnce;
    }

    static void RunOnce()
    {
        EditorApplication.update -= RunOnce;

        string prefabPath = "Assets/Resources/RulesCanvas.prefab";
        if (File.Exists(prefabPath))
        {
            // Already created
            return;
        }

        if (!Directory.Exists("Assets/Resources"))
        {
            Directory.CreateDirectory("Assets/Resources");
        }

        // Create root Canvas
        GameObject root = new GameObject("RulesCanvas");
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(2f, 2f); // 2 meters by 2 meters roughly? Wait, 2x2 might be too big or small depending on scale.
        // Let's make the canvas 800x800 and scale it down to world space.
        rt.sizeDelta = new Vector2(800, 800);
        rt.localScale = new Vector3(0.002f, 0.002f, 0.002f); // 1.6m x 1.6m
        root.AddComponent<CanvasScaler>();

        // Create Scroll View (similar to what Unity's UI > Scroll View does)
        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(root.transform, false);
        RectTransform viewportRt = viewport.AddComponent<RectTransform>();
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.sizeDelta = Vector2.zero;
        viewportRt.pivot = new Vector2(0.5f, 0.5f);
        
        viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0.8f); // Dark background
        viewport.AddComponent<RectMask2D>();

        // Create Content (Text)
        GameObject content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRt = content.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0, 1);
        contentRt.anchorMax = new Vector2(1, 1);
        contentRt.pivot = new Vector2(0.5f, 1);
        contentRt.sizeDelta = new Vector2(0, 2000); // Height will be driven by ContentSizeFitter
        contentRt.anchoredPosition = Vector2.zero;

        TextMeshProUGUI text = content.AddComponent<TextMeshProUGUI>();
        text.text = "Rules loading...";
        TextAsset rulesAsset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/BASHRules.txt");
        if (rulesAsset != null)
        {
            text.text = rulesAsset.text;
        }
        else
        {
            // Fallback try reading directly
            if (File.Exists("Assets/Resources/BASHRules.txt"))
            {
                text.text = File.ReadAllText("Assets/Resources/BASHRules.txt");
            }
        }
        text.fontSize = 24;
        text.color = Color.white;
        text.margin = new Vector4(20, 20, 20, 20); // Add padding
        
        ContentSizeFitter csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Add ScrollRect to root
        ScrollRect scrollRect = root.AddComponent<ScrollRect>();
        scrollRect.content = contentRt;
        scrollRect.viewport = viewportRt;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 15f;

        // Add our custom joystick scroller
        ScrollTextWithJoystick scroller = root.AddComponent<ScrollTextWithJoystick>();
        scroller.scrollRect = scrollRect;
        scroller.scrollSpeed = 1.5f;

        // Save as prefab
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

        // Destroy temporary scene object
        Object.DestroyImmediate(root);

        Debug.Log("Created RulesCanvas prefab successfully.");
    }
}
