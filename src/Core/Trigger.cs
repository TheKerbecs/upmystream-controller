using System;
using System.Runtime.Serialization;

namespace GameController;

/// <summary>What fires (a redeem or an exact chat message) and what it does (key, mouse button, scroll, move).</summary>
[DataContract]
public sealed class Trigger
{
    public const string Redeem = "redeem", Chat = "chat";
    public const string KeyAction = "key", MouseAction = "mouse", ScrollAction = "scroll", MoveAction = "move";
    public static readonly string[] Buttons = { "Left", "Right", "Middle", "Back", "Forward" };
    public const int HoldMax = 10000, MoveMax = 5000, MoveMsMax = 5000, NotchesMax = 20;

    [DataMember(Name = "id")] public string Id;
    [DataMember(Name = "enabled")] public bool Enabled;
    [DataMember(Name = "source")] public string Source;
    [DataMember(Name = "match")] public string Match;
    [DataMember(Name = "action")] public string Action;
    [DataMember(Name = "keys")] public string Keys; // key action: the combo; mouse action: optional Ctrl/Shift/Alt
    [DataMember(Name = "button")] public string Button;
    [DataMember(Name = "holdMs")] public int HoldMs;
    [DataMember(Name = "notches")] public int Notches; // + up, - down
    [DataMember(Name = "dx")] public int Dx;
    [DataMember(Name = "dy")] public int Dy;
    [DataMember(Name = "moveMs")] public int MoveMs;
    [DataMember(Name = "cooldownS")] public int CooldownS;

    public Trigger() => Defaults();

    // The JSON reader skips constructors and field initializers: fill what an older file did not have.
    [OnDeserializing]
    void OnDeserializing(StreamingContext _) => Defaults();

    void Defaults()
    {
        Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        Enabled = true;
        Source = Redeem;
        Match = "";
        Action = KeyAction;
        Keys = "";
        Button = "Left";
        HoldMs = 50;
        Notches = -1;
        MoveMs = 200;
    }

    public static Trigger FromMapping(Mapping m) => new Trigger
    {
        Enabled = m.Enabled,
        Source = Redeem,
        Match = m.Reward ?? "",
        Action = KeyAction,
        Keys = m.Keys ?? "",
        HoldMs = m.HoldMs,
        CooldownS = m.CooldownS,
    };

    public bool Matches(StreamEvent ev)
    {
        var match = (Match ?? "").Trim();
        if (match.Length == 0) return false;
        return ev.IsChat
            ? Source == Chat && string.Equals(match, (ev.Message ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
            : Source == Redeem && string.Equals(match, (ev.RedeemName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The checked, clamped action, or an error a person can act on.</summary>
    public bool TryPlan(out ActionPlan plan, out string error)
    {
        plan = null;
        error = null;
        var hold = Math.Max(1, Math.Min(HoldMax, HoldMs));
        switch (Action)
        {
            case KeyAction:
                if (!Combo.TryParse(Keys, out var combo, out error)) return false;
                plan = new ActionPlan { Kind = KeyAction, Combo = combo, HoldMs = hold };
                return true;
            case MouseAction:
                if (Array.IndexOf(Buttons, Button) < 0)
                {
                    error = "pick a mouse button";
                    return false;
                }
                Combo mods = null;
                if (!string.IsNullOrWhiteSpace(Keys) && !Combo.TryParseModifiers(Keys, out mods, out error)) return false;
                plan = new ActionPlan { Kind = MouseAction, Button = Button, Combo = mods, HoldMs = hold };
                return true;
            case ScrollAction:
                if (Notches == 0)
                {
                    error = "scroll at least one notch";
                    return false;
                }
                plan = new ActionPlan { Kind = ScrollAction, Notches = Math.Max(-NotchesMax, Math.Min(NotchesMax, Notches)) };
                return true;
            case MoveAction:
                if (Dx == 0 && Dy == 0)
                {
                    error = "set a distance to move";
                    return false;
                }
                plan = new ActionPlan
                {
                    Kind = MoveAction,
                    Dx = Math.Max(-MoveMax, Math.Min(MoveMax, Dx)),
                    Dy = Math.Max(-MoveMax, Math.Min(MoveMax, Dy)),
                    MoveMs = Math.Max(0, Math.Min(MoveMsMax, MoveMs)),
                };
                return true;
            default:
                error = "pick an action";
                return false;
        }
    }
}

/// <summary>A validated action, ready for the input sender.</summary>
public sealed class ActionPlan
{
    public string Kind;
    public Combo Combo; // key: the combo; mouse: modifiers held around the click (may be null)
    public string Button;
    public int HoldMs, Notches, Dx, Dy, MoveMs;

    public override string ToString()
    {
        switch (Kind)
        {
            case Trigger.KeyAction: return $"{Combo} · {HoldMs} ms";
            case Trigger.MouseAction: return (Combo == null ? "" : Combo + "+") + Button + " click · " + HoldMs + " ms";
            case Trigger.ScrollAction: return $"Scroll {(Notches > 0 ? "up" : "down")} ×{Math.Abs(Notches)}";
            default: return $"Move {Dx}, {Dy} · {MoveMs} ms";
        }
    }
}
