using System.Collections.Generic;

namespace GameController;

public sealed class SseEvent
{
    public string Name;
    public string Data;
}

/// <summary>Server-sent events, one line at a time (WHATWG rules for the fields we use).</summary>
public sealed class SseParser
{
    string _name;
    readonly List<string> _data = new List<string>();

    /// <summary>Feed one line without its line ending. Returns an event when a blank line ends one.</summary>
    public SseEvent Line(string line)
    {
        if (line.Length == 0)
        {
            SseEvent ev = null;
            if (_data.Count > 0) ev = new SseEvent { Name = _name ?? "message", Data = string.Join("\n", _data) };
            _name = null;
            _data.Clear();
            return ev;
        }
        if (line[0] == ':') return null; // comment, e.g. ": ping"
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line.Substring(0, colon);
        var value = colon < 0 ? "" : line.Substring(colon + 1);
        if (value.StartsWith(" ")) value = value.Substring(1);
        if (field == "event") _name = value;
        else if (field == "data") _data.Add(value);
        return null;
    }
}
