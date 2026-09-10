using UnityEngine;

/// <summary>
/// One line in a <see cref="ScrollList"/>: what it says, and the stable id it acts on.
///
/// <see cref="id"/> is what lands on the row's keyInfo.keyName and therefore what the pointer
/// reports when the row is pressed. It must be the thing itself — a GameModule.gameKey, a lobby id,
/// an action key — and never a slot index: the list recycles a fixed set of row objects, so the
/// object under the beam keeps its identity while the data behind it changes.
/// </summary>
public class RowData
{
    public string id = "";
    public string title = "";
    public string subtitle = "";

    /// <summary>The right-hand cell: "In Library", a price, "3/12", "ON"/"OFF".</summary>
    public string state = "";

    public Sprite thumbnail;

    /// <summary>
    /// A pressable row. False greys it and switches its collider off, so a row that cannot be
    /// acted on cannot be pressed either — the reason belongs in <see cref="state"/>, where the
    /// player can read it, rather than in a message that only appears after a dead press.
    /// </summary>
    public bool pressable = true;

    /// <summary>Draw as the current choice. Independent of <see cref="pressable"/>.</summary>
    public bool selected = false;

    /// <summary>Anything the caller wants back when this row is pressed. Never sent anywhere.</summary>
    public object payload;

    public RowData() { }

    public RowData(string id, string title, string state = "", string subtitle = "")
    {
        this.id = id;
        this.title = title;
        this.state = state;
        this.subtitle = subtitle;
    }
}
