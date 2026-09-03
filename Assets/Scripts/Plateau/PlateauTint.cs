using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Per-instance colour on a plateau, a piece or a bridge spot, via a MaterialPropertyBlock.
///
/// Deliberately NOT the keyInfo material-swap idiom, for four reasons:
///
///  - Every piece needs its owner's colour anyway. Twelve seats x three prefabs is 36 material
///    assets to hand-author with a swap; here it is one shared material and one colour write.
///  - keyInfo uses GetComponent&lt;MeshRenderer&gt;().material, the INSTANTIATING accessor. It gets
///    away with it on three menu keys; doing it to 41 plateaus creates 41 material instances and
///    41 leaks, on a mobile GPU rendering stereo.
///  - 40 of the 41 plateaus carry a per-instance 15/35/50 material override — the only semantic
///    data in ChasmGame. A swap would destroy it.
///  - A swap replaces information; a tint composes with it. A highlighted piece must still show
///    whose it is.
///
/// One property block, reused. Never `new MaterialPropertyBlock()` per call — that is a per-frame
/// allocation on every highlighted object.
/// </summary>
[DisallowMultipleComponent]
public class PlateauTint : MonoBehaviour
{
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId     = Shader.PropertyToID("_Color");

    Renderer[] renderers;
    Color[] baseColors;
    int[] propertyIds;
    bool[] overridden;          // true where SetBaseColor replaced the authored colour
    bool highlighted;

    MaterialPropertyBlock block;

    /// <summary>
    /// Cache the renderers and their authored colours. Idempotent; call it once after the object
    /// is built. TextMeshPro renderers are skipped — TMP colour is a vertex colour, set directly
    /// on the component, and pushing a property block at it would fight the text renderer.
    /// </summary>
    public void Capture()
    {
        if (renderers != null)
        {
            return;
        }

        List<Renderer> found = new List<Renderer>();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.sharedMaterial == null)
            {
                continue;
            }
            if (r.GetComponent<TMP_Text>() != null)
            {
                continue;
            }
            found.Add(r);
        }

        renderers   = found.ToArray();
        baseColors  = new Color[renderers.Length];
        propertyIds = new int[renderers.Length];
        overridden  = new bool[renderers.Length];
        block       = new MaterialPropertyBlock();

        for (int i = 0; i < renderers.Length; i++)
        {
            Material m = renderers[i].sharedMaterial;
            // Every material in this project is URP/Lit, so _BaseColor is always there. The
            // fallback is for anything dropped in later on a Built-in or Unlit shader.
            propertyIds[i] = m.HasProperty(BaseColorId) ? BaseColorId : ColorId;
            baseColors[i]  = m.HasProperty(propertyIds[i]) ? m.GetColor(propertyIds[i]) : Color.white;
        }
    }

    /// <summary>
    /// Replace one renderer's resting colour — used for a piece's owner disc, which must keep
    /// showing whose it is underneath any highlight.
    /// </summary>
    public void SetBaseColor(Renderer target, Color color)
    {
        Capture();
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != target)
            {
                continue;
            }
            baseColors[i] = color;
            overridden[i] = true;
            Apply(Color.clear, 0f);
            return;
        }
    }

    /// <summary>Lerp every renderer from its resting colour toward <paramref name="target"/>.</summary>
    public void SetHighlight(Color target, float t)
    {
        Capture();
        highlighted = t > 0f;
        Apply(target, t);
    }

    /// <summary>Back to the resting colour.</summary>
    public void ClearHighlight()
    {
        if (renderers == null || !highlighted)
        {
            return;
        }
        highlighted = false;
        Apply(Color.clear, 0f);
    }

    void Apply(Color target, float t)
    {
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
            {
                continue;
            }

            // Nothing to say about this renderer: drop the block entirely rather than writing the
            // authored colour back, so the renderer rejoins the SRP batcher.
            if (t <= 0f && !overridden[i])
            {
                r.SetPropertyBlock(null);
                continue;
            }

            r.GetPropertyBlock(block);

            Color c = t <= 0f ? baseColors[i] : Color.Lerp(baseColors[i], target, t);
            // Keep the resting alpha: it is what makes the owner disc visible and the plateau
            // opaque, and a highlight colour has no business changing either.
            c.a = baseColors[i].a;
            block.SetColor(propertyIds[i], c);

            r.SetPropertyBlock(block);
        }
    }

    void OnDisable()
    {
        // A scene switch destroys these objects; a pooled or reparented one must not keep a stale
        // highlight. Overrides survive, because they are what the object IS, not a highlight.
        if (renderers == null)
        {
            return;
        }
        highlighted = false;
        Apply(Color.clear, 0f);
    }

    /// <summary>Drop the cache so the next Capture re-reads the renderers. For a rebuilt piece.</summary>
    public void Invalidate()
    {
        renderers = null;
        highlighted = false;
    }
}
