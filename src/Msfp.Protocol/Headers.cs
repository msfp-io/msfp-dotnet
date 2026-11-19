namespace Msfp.Protocol;

/// <summary>
/// The headers of a request or answer as MSFP reads them: by name, case
/// insensitive, every value it was sent with. Hosts adapt their own
/// header collection (ASP.NET Core's, HttpClient's) or fill a
/// <see cref="HeaderList"/>.
/// </summary>
public interface IHeaderReader
{
    /// <summary>Every value sent under the name, in order; empty for none.</summary>
    IReadOnlyList<string> GetValues(string name);
}

/// <summary>A plain list of headers, case-insensitive by name.</summary>
public sealed class HeaderList : IHeaderReader
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    /// <summary>Every header, in the order added.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    /// <summary>Adds a value under the name, after any others.</summary>
    public HeaderList Add(string name, string value)
    {
        _items.Add(new(name, value));
        return this;
    }

    /// <summary>Replaces every value under the name with this one.</summary>
    public HeaderList Set(string name, string value)
    {
        Remove(name);
        return Add(name, value);
    }

    /// <summary>Removes every value under the name.</summary>
    public HeaderList Remove(string name)
    {
        _items.RemoveAll(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>The first value under the name, or null.</summary>
    public string? Get(string name)
    {
        var v = GetValues(name);
        return v.Count == 0 ? null : v[0];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetValues(string name) =>
        _items.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();

    /// <summary>A copy.</summary>
    public HeaderList Clone()
    {
        var c = new HeaderList();
        c._items.AddRange(_items);
        return c;
    }
}
