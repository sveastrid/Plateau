using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Per-instance highlight on a cell, a step or a pawn, via a MaterialPropertyBlock.
///
/// NOT a material swap, for the reason PlateauTint gives: keyInfo's idiom uses the *instantiating*
/// Renderer.material accessor, which is harmless on three menu keys and 64 material instances and
/// 64 leaks on a board of cells, on a mobile GPU rendering stereo. A tint also composes with what is
/// already there, so a highlighted step still shows whose tiles they are.
///
/// This is a deliberate second copy rather than a shared component. MRBoardGame.Stairs cannot see
/// MRBoardGame.Plateau — that boundary is the point — and lifting PlateauTint into Shared would mean
/// editing Chasms, which is outside this game's world. It is the leaner half of PlateauTint: Stairs
/// needs no per-renderer base-colour override, because Step1/Step2 and Player1/Player2 already carry
/// the two seats' colours as authored materials.
///
/// One property block, reused. Never `new MaterialPropertyBlock()` per call — that is a per-frame
/// allocation on every highlighted object, and a whole board can be lit at once.
/// </summary>
[DisallowMultipleComponent]
public class StairsTint : MonoBehaviour
{
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    Renderer[] renderers;
    Color[] baseColors;
    int[] propertyIds;
    bool highlighted;

    MaterialPropertyBlock block;

    /// <summary>Add one if it is not there yet. Cells are authored scene objects and pieces are
    /// prefab instances, so neither carries this until something wants to light it.</summary>
    public static StairsTint Attach(GameObject go)
    {
        StairsTint tint = go.GetComponent<StairsTint>();
        if (tint == null)
        {
            tint = go.AddComponent<StairsTint>();
        }
        tint.Capture();
        return tint;
    }

    /// <summary>
    /// Cache the renderers and their authored colours. Idempotent; call it once the object is built.
    /// TextMeshPro renderers are skipped — TMP colour is a vertex colour set on the component, and
    /// pushing a property block at it fights the text renderer.
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

        renderers = found.ToArray();
        baseColors = new Color[renderers.Length];
        propertyIds = new int[renderers.Length];
        block = new MaterialPropertyBlock();

        for (int i = 0; i < renderers.Length; i++)
        {
            Material m = renderers[i].sharedMaterial;
            // Every material in this project is URP/Lit, so _BaseColor is always there. The
            // fallback is for anything dropped in later on a Built-in or Unlit shader.
            propertyIds[i] = m.HasProperty(BaseColorId) ? BaseColorId : ColorId;
            baseColors[i] = m.HasProperty(propertyIds[i]) ? m.GetColor(propertyIds[i]) : Color.white;
        }
    }

    /// <summary>Lerp every renderer from its authored colour toward <paramref name="target"/>.</summary>
    public void SetHighlight(Color target, float t)
    {
        Capture();
        highlighted = t > 0f;
        Apply(target, t);
    }

    /// <summary>Back to the authored colour, and back into the SRP batcher with it.</summary>
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
            if (t <= 0f)
            {
                r.SetPropertyBlock(null);
                continue;
            }

            r.GetPropertyBlock(block);

            Color c = Color.Lerp(baseColors[i], target, t);
            // Keep the resting alpha. ClearWhite.mat on the pawn's disc is alpha 0.035 on purpose,
            // and a highlight colour has no business making it opaque.
            c.a = baseColors[i].a;
            block.SetColor(propertyIds[i], c);

            r.SetPropertyBlock(block);
        }
    }

    void OnDisable()
    {
        // A game switch destroys these; a pooled or reparented one must not keep a stale highlight.
        if (renderers == null)
        {
            return;
        }
        highlighted = false;
        Apply(Color.clear, 0f);
    }
}
