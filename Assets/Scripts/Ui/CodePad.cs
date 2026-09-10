using System;
using System.Text;
using UnityEngine;
using TMPro;

/// <summary>
/// The compact character pad that replaced the 40-key lobby keyboard: A-Z and 0-9 in a 6 x 6 grid,
/// plus Back, Clear, Cancel and a submit key.
///
/// It exists for exactly two jobs — a six-character Relay join code and a username — and it is
/// sized for them. The alphabet is deliberately wider than the Relay join-code alphabet rather
/// than narrower: a code containing a character the pad cannot type is unjoinable and nothing says
/// so, whereas a spare key that never appears in a code is invisible. A code that does not exist
/// already fails honestly, through RelayServiceException into GameController.ShowJoinFailed.
///
/// The keys are cloned from an inactive template in the prefab, MenuControl's idiom, so the
/// collider, rigidbody, colours and label scale are authored rather than set from code.
/// </summary>
public class CodePad : MonoBehaviour
{
    public const string BackKey = "Back";
    public const string ClearKey = "Clear";
    public const string CancelKey = "Cancel";

    public Panel panel;
    public RectTransform grid;

    [Tooltip("Inactive in the prefab. Cloned once per character.")]
    public keyInfo padKeyTemplate;

    [Tooltip("Shows what has been typed so far.")]
    public TMP_Text entry;

    public int columns = 6;
    public float cellWidth = 150f;
    public float cellHeight = 110f;

    [Tooltip("A-Z 0-9 fills a 6 x 6 grid exactly. Controls go on the row below it.")]
    public string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>The typed text, or null when the player pressed Cancel.</summary>
    public event Action<string> Submitted;

    private readonly StringBuilder typed = new StringBuilder();
    private string submitKey = "Join";
    private int maxLength = 6;
    private bool built;

    void Awake()
    {
        Build();
    }

    /// <summary>
    /// Show the pad. <paramref name="submitLabel"/> becomes the submit key's keyName as well as its
    /// label, so a pad opened for a username and one opened for a room code cannot be confused.
    /// </summary>
    public void Open(string title, string submitLabel, int maxChars, string seed = "")
    {
        Build();

        submitKey = string.IsNullOrEmpty(submitLabel) ? "OK" : submitLabel;
        maxLength = Mathf.Max(1, maxChars);

        typed.Length = 0;
        if (!string.IsNullOrEmpty(seed))
        {
            typed.Append(seed.Length > maxLength ? seed.Substring(0, maxLength) : seed);
        }

        if (panel != null)
        {
            panel.SetHeader(title);
            panel.SetStatus("");
        }

        RelabelSubmit();
        ShowEntry();
        gameObject.SetActive(true);
    }

    public void Close()
    {
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Called by whoever owns the pad's Panel.KeyPressed. Returns true when the key was one of the
    /// pad's, so the owner can fall through to its own keys for anything else.
    /// </summary>
    public bool HandleKey(string keyName)
    {
        if (string.IsNullOrEmpty(keyName))
        {
            return false;
        }

        if (keyName == submitKey)
        {
            string value = typed.ToString();
            Close();
            Submitted?.Invoke(value);
            return true;
        }

        switch (keyName)
        {
            case CancelKey:
                Close();
                Submitted?.Invoke(null);
                return true;

            case ClearKey:
                typed.Length = 0;
                ShowEntry();
                return true;

            case BackKey:
                if (typed.Length > 0)
                {
                    typed.Length -= 1;
                }
                ShowEntry();
                return true;
        }

        if (keyName.Length != 1)
        {
            return false;                     // not one of ours
        }

        if (typed.Length < maxLength)
        {
            typed.Append(keyName);
            ShowEntry();
        }
        return true;
    }

    private void ShowEntry()
    {
        if (entry != null)
        {
            entry.SetText(typed.Length == 0 ? "_" : typed.ToString());
        }
    }

    private void Build()
    {
        if (built)
        {
            return;
        }

        if (padKeyTemplate == null || grid == null)
        {
            Debug.LogError("CodePad on " + name + ": padKeyTemplate or grid is not assigned, so " +
                           "the pad has no keys and nothing can be typed.");
            return;
        }

        built = true;

        string keys = alphabet ?? "";
        for (int i = 0; i < keys.Length; i++)
        {
            Clone(keys[i].ToString(), i / columns, i % columns);
        }

        int controlRow = (keys.Length + columns - 1) / columns;
        Clone(BackKey, controlRow, 0);
        Clone(ClearKey, controlRow, 1);
        Clone(CancelKey, controlRow, 2);
        submitKeyObject = Clone(submitKey, controlRow, 4);
    }

    private keyInfo submitKeyObject;

    private keyInfo Clone(string keyName, int row, int column)
    {
        GameObject clone = Instantiate(padKeyTemplate.gameObject, grid);
        clone.name = keyName;
        clone.SetActive(true);

        RectTransform rt = clone.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(column * cellWidth, -row * cellHeight);
        }

        keyInfo info = clone.GetComponent<keyInfo>();
        if (info == null)
        {
            Debug.LogError("CodePad: the key template has no keyInfo, so '" + keyName +
                           "' can never be pressed.");
            return null;
        }

        // Left with overrideNameChange false on purpose: every key here shows its own keyName, so
        // keyInfo.Start() writing the label is exactly the wanted behaviour. Only the submit key,
        // whose name changes between opens, is relabelled by hand.
        info.keyName = keyName;
        if (info.keyLabel != null)
        {
            info.keyLabel.SetText(keyName);
        }
        return info;
    }

    private void RelabelSubmit()
    {
        if (submitKeyObject == null)
        {
            return;
        }

        submitKeyObject.keyName = submitKey;
        submitKeyObject.overrideNameChange = true;
        submitKeyObject.gameObject.name = submitKey;
        if (submitKeyObject.keyLabel != null)
        {
            submitKeyObject.keyLabel.SetText(submitKey);
        }
    }
}
