namespace MeetVault.Views;

// Batch-delete selection state shared by the Dashboard and Meetings lists.
// Keyed by immutable meeting id; pages are recreated on navigation, so a
// screen switch always starts from a clean inactive state (no stale hidden
// selections).
public sealed class MeetingSelection
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public bool IsActive { get; private set; }
    public int Count => _ids.Count;
    public IReadOnlyCollection<string> Ids => _ids;

    public bool Contains(string id) => _ids.Contains(id);

    public void Begin()
    {
        _ids.Clear();
        IsActive = true;
    }

    // Returns the new selected state of the id.
    public bool Toggle(string id)
    {
        if (_ids.Add(id)) return true;
        _ids.Remove(id);
        return false;
    }

    public void Select(string id) => _ids.Add(id);

    public void SelectAll(IEnumerable<string> ids)
    {
        foreach (var id in ids) _ids.Add(id);
    }

    public void Clear() => _ids.Clear();

    public void End()
    {
        _ids.Clear();
        IsActive = false;
    }
}
