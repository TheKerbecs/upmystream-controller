using System;
using System.Collections.Generic;
using System.Linq;

namespace GameController;

/// <summary>A key or combo such as "Shift+Q": modifiers (virtual-key codes) plus one main key.</summary>
public sealed class Combo
{
    public const int Ctrl = 0x11, Shift = 0x10, Alt = 0x12;
    static readonly int[] ModifierOrder = { Ctrl, Shift, Alt };

    static readonly Dictionary<string, int> Names = BuildNames();
    static readonly Dictionary<int, string> ByVk = Names.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);

    // Combos that leave or wreck the game. Blocked when the key matches and these modifiers are all held.
    static readonly (int[] mods, int key)[] Blocked =
    {
        (new[] { Alt }, 0x73), // Alt+F4
        (new[] { Alt }, 0x09), // Alt+Tab
        (new[] { Alt }, 0x1B), // Alt+Esc
        (new[] { Ctrl }, 0x1B), // Ctrl+Esc, Ctrl+Shift+Esc
        (new[] { Ctrl, Alt }, 0x2E), // Ctrl+Alt+Delete
    };

    public const string NotAllowed = "not allowed, it would close or leave the game";

    public IReadOnlyList<int> Modifiers { get; }
    public int Key { get; }

    Combo(IReadOnlyList<int> modifiers, int key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>0 for a modifiers-only combo (held around a mouse click).</summary>
    public override string ToString() => string.Join("+", Modifiers.Concat(Key == 0 ? new int[0] : new[] { Key }).Select(vk => ByVk[vk]));

    /// <summary>"Shift", "Ctrl+Alt": modifiers to hold around a mouse click.</summary>
    public static bool TryParseModifiers(string text, out Combo combo, out string error)
    {
        combo = null;
        if (!TryTokens(text, out var vks, out error)) return false;
        if (vks.Any(vk => !ModifierOrder.Contains(vk)))
        {
            error = "only Ctrl, Shift and Alt can be held while clicking";
            return false;
        }
        combo = new Combo(ModifierOrder.Where(vks.Contains).ToList(), 0);
        return true;
    }

    /// <summary>The on/off hotkey: at least one of Ctrl/Shift/Alt plus one other key, so it never fires mid-game
    /// by accident. Returns MOD_* flags for RegisterHotKey.</summary>
    public static bool TryParseHotkey(string text, out uint mods, out int vk, out string error)
    {
        mods = 0;
        vk = 0;
        if (!TryTokens(text, out var vks, out error)) return false;
        var main = vks.Where(v => !ModifierOrder.Contains(v)).ToList();
        if (main.Count != 1 || main.Count == vks.Count)
        {
            error = "a hotkey needs Ctrl, Shift or Alt plus one other key, for example Ctrl+Shift+F12";
            return false;
        }
        vk = main[0];
        var key = vk;
        if (Blocked.Any(b => b.key == key && b.mods.All(vks.Contains)))
        {
            error = NotAllowed;
            return false;
        }
        if (vks.Contains(Alt)) mods |= 0x1;
        if (vks.Contains(Ctrl)) mods |= 0x2;
        if (vks.Contains(Shift)) mods |= 0x4;
        return true;
    }

    static bool TryTokens(string text, out List<int> vks, out string error)
    {
        vks = new List<int>();
        error = null;
        var tokens = (text ?? "").Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
        if (tokens.Count == 0)
        {
            error = "no key set";
            return false;
        }
        foreach (var t in tokens)
        {
            if (!Names.TryGetValue(t.ToLowerInvariant(), out var vk))
            {
                error = $"unknown key '{t}'";
                return false;
            }
            if (!vks.Contains(vk)) vks.Add(vk);
        }
        return true;
    }

    /// <summary>Parses "ctrl+shift+q" and friends. Win keys have no name, so they can never be mapped.</summary>
    public static bool TryParse(string text, out Combo combo, out string error)
    {
        combo = null;
        if (!TryTokens(text, out var vks, out error)) return false;
        var main = vks.Where(vk => !ModifierOrder.Contains(vk)).ToList();
        if (main.Count > 1)
        {
            error = "use only one key besides Ctrl, Shift and Alt";
            return false;
        }
        int key;
        List<int> mods;
        if (main.Count == 1)
        {
            key = main[0];
            mods = ModifierOrder.Where(vks.Contains).ToList();
        }
        else // modifiers only: the last one is the key, e.g. "Shift" to sprint
        {
            var ordered = ModifierOrder.Where(vks.Contains).ToList();
            key = ordered[ordered.Count - 1];
            mods = ordered.Take(ordered.Count - 1).ToList();
        }
        if (Blocked.Any(b => b.key == key && b.mods.All(mods.Contains)))
        {
            error = NotAllowed;
            return false;
        }
        combo = new Combo(mods, key);
        error = null;
        return true;
    }

    /// <summary>Keys sharing a scan code with the numpad (Left = Num4 without the flag). MapVirtualKey's E0 prefix
    /// is not reliable for these (seen on Windows 11 26300), so the list is fixed.</summary>
    public static bool IsExtended(int vk) =>
        vk is >= 0x21 and <= 0x28 // PageUp, PageDown, End, Home, arrows
        or 0x2D or 0x2E // Insert, Delete
        or 0x6F; // NumDivide

    /// <summary>The name of a virtual-key code, or null when it cannot be mapped.</summary>
    public static string NameOf(int vk) => ByVk.TryGetValue(vk, out var n) ? n : null;

    static Dictionary<string, int> BuildNames()
    {
        var d = new Dictionary<string, int>();
        void Add(string name, int vk) => d[name] = vk;
        Add("Ctrl", Ctrl);
        Add("Shift", Shift);
        Add("Alt", Alt);
        for (var c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
        for (var i = 0; i <= 9; i++) Add(i.ToString(), 0x30 + i);
        for (var i = 1; i <= 24; i++) Add("F" + i, 0x6F + i);
        for (var i = 0; i <= 9; i++) Add("Num" + i, 0x60 + i);
        Add("Space", 0x20);
        Add("Enter", 0x0D);
        Add("Tab", 0x09);
        Add("Esc", 0x1B);
        Add("Backspace", 0x08);
        Add("CapsLock", 0x14);
        Add("Up", 0x26);
        Add("Down", 0x28);
        Add("Left", 0x25);
        Add("Right", 0x27);
        Add("Insert", 0x2D);
        Add("Delete", 0x2E);
        Add("Home", 0x24);
        Add("End", 0x23);
        Add("PageUp", 0x21);
        Add("PageDown", 0x22);
        Add("NumMultiply", 0x6A);
        Add("NumAdd", 0x6B);
        Add("NumSubtract", 0x6D);
        Add("NumDecimal", 0x6E);
        Add("NumDivide", 0x6F);
        Add("Semicolon", 0xBA);
        Add("Equals", 0xBB);
        Add("Comma", 0xBC);
        Add("Minus", 0xBD);
        Add("Period", 0xBE);
        Add("Slash", 0xBF);
        Add("Backquote", 0xC0);
        Add("LeftBracket", 0xDB);
        Add("Backslash", 0xDC);
        Add("RightBracket", 0xDD);
        Add("Quote", 0xDE);
        // Display names above win in ByVk (first added); lowercase aliases make parsing case-insensitive.
        foreach (var p in d.ToList()) d[p.Key.ToLowerInvariant()] = p.Value;
        d["control"] = Ctrl;
        d["escape"] = 0x1B;
        d["return"] = 0x0D;
        d["del"] = 0x2E;
        return d;
    }
}
