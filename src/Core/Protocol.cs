using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GameController;

/// <summary>Page → host. Only the fields a type needs are set.</summary>
[DataContract]
public sealed class UiRequest
{
    // ready, set-key, test-key, list-games, set-game, save-trigger, delete-trigger, set-chat, set-hotkey,
    // start, stop, pause, check-keys, capture, run-now
    [DataMember(Name = "type")] public string Type;
    [DataMember(Name = "text")] public string Text; // key, game, keys to check, hotkey
    [DataMember(Name = "id")] public string Id;
    [DataMember(Name = "on")] public bool On;
    [DataMember(Name = "trigger")] public Trigger Trigger;
}

/// <summary>Host → page: everything the page shows, sent whole on every change (it is small).</summary>
[DataContract]
public sealed class UiState
{
    [DataMember(Name = "type")] public string Type = "state";
    [DataMember(Name = "version")] public string Version;
    [DataMember(Name = "keyHint")] public string KeyHint; // "••••abcd", or "" without a key
    [DataMember(Name = "status")] public string Status; // stopped, connecting, connected, reconnecting, badkey
    [DataMember(Name = "detail")] public string Detail;
    [DataMember(Name = "running")] public bool Running;
    [DataMember(Name = "paused")] public bool Paused;
    [DataMember(Name = "chat")] public bool Chat;
    [DataMember(Name = "hotkey")] public string Hotkey;
    [DataMember(Name = "game")] public string Game;
    [DataMember(Name = "games")] public List<string> Games = new List<string>();
    [DataMember(Name = "redeems")] public List<string> Redeems = new List<string>();
    [DataMember(Name = "triggers")] public List<TriggerView> Triggers = new List<TriggerView>();
    [DataMember(Name = "log")] public List<string> Log = new List<string>();
}

[DataContract]
public sealed class TriggerView
{
    [DataMember(Name = "trigger")] public Trigger Trigger;
    [DataMember(Name = "summary")] public string Summary; // "Shift+Q · 50 ms", or empty
    [DataMember(Name = "problem")] public string Problem; // why it can't fire yet, or empty

    public static TriggerView Of(Trigger t)
    {
        var ok = t.TryPlan(out var plan, out var error);
        if (ok && string.IsNullOrWhiteSpace(t.Match)) return new TriggerView { Trigger = t, Summary = plan.ToString(), Problem = "no redeem or chat message set" };
        return new TriggerView { Trigger = t, Summary = ok ? plan.ToString() : "", Problem = ok ? "" : error };
    }
}

/// <summary>Host → page: one-off answers (key test, key check).</summary>
[DataContract]
public sealed class UiReply
{
    [DataMember(Name = "type")] public string Type;
    [DataMember(Name = "ok")] public bool Ok;
    [DataMember(Name = "text")] public string Text;
    [DataMember(Name = "error")] public string Error;
}
