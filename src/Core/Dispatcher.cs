using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GameController;

/// <summary>Turns stream events into actions: trigger lookup, cooldown and queue cap on arrival; pause and focus
/// checks again right before acting. One action at a time.</summary>
public sealed class Dispatcher : IDisposable
{
    public const int QueueMax = 5;

    readonly Action<ActionPlan> _run;
    readonly Func<bool> _gameFocused;
    readonly Action<string> _log;
    readonly Func<DateTime> _now;
    readonly BlockingCollection<(string who, ActionPlan plan)> _queue = new BlockingCollection<(string, ActionPlan)>(QueueMax);
    readonly Dictionary<string, DateTime> _lastFired = new Dictionary<string, DateTime>();
    volatile List<Trigger> _triggers = new List<Trigger>();

    public volatile bool Paused;
    public volatile bool ChatEnabled;

    public Dispatcher(Action<ActionPlan> run, Func<bool> gameFocused, Action<string> log,
        Func<DateTime> now = null, bool startWorker = true)
    {
        _run = run;
        _gameFocused = gameFocused;
        _log = log;
        _now = now ?? (() => DateTime.UtcNow);
        if (!startWorker) return;
        new Thread(() => { while (RunOne(Timeout.Infinite)) { } }) { IsBackground = true, Name = "actions" }.Start();
    }

    public void SetTriggers(IEnumerable<Trigger> triggers) => _triggers = triggers.ToList();

    public void Handle(StreamEvent ev)
    {
        if (ev.IsChat && !ChatEnabled) return;
        var t = _triggers.FirstOrDefault(x => x.Enabled && x.Matches(ev));
        var who = ev.IsChat ? $"{ev.Username} (chat) → {ev.Message?.Trim()}" : $"{ev.Username} → {ev.RedeemName}";
        if (t == null)
        {
            if (!ev.IsChat) _log($"{who}: no trigger for this redeem"); // most chat lines are just chat
            return;
        }
        if (Paused)
        {
            _log($"{who}: skipped (paused)");
            return;
        }
        if (!t.TryPlan(out var plan, out var error))
        {
            _log($"{who}: {error}");
            return;
        }
        var now = _now();
        lock (_lastFired)
        {
            if (t.CooldownS > 0 && _lastFired.TryGetValue(t.Id, out var last) && now - last < TimeSpan.FromSeconds(t.CooldownS))
            {
                _log($"{who}: skipped (cooldown)");
                return;
            }
            if (_queue.IsAddingCompleted) return;
            if (!_queue.TryAdd((who, plan)))
            {
                _log($"{who}: dropped ({QueueMax} actions already queued)");
                return;
            }
            _lastFired[t.Id] = now;
        }
    }

    /// <summary>Runs the next queued action. False once the dispatcher is disposed or the wait timed out.</summary>
    public bool RunOne(int timeoutMs)
    {
        (string who, ActionPlan plan) item;
        try
        {
            if (!_queue.TryTake(out item, timeoutMs)) return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException) // CompleteAdding was called
        {
            return false;
        }
        if (Paused) _log($"{item.who}: skipped (paused)");
        else if (!_gameFocused()) _log($"{item.who}: skipped (game not active)");
        else
        {
            try
            {
                _run(item.plan);
                _log($"{item.who} → {item.plan}");
            }
            catch (Exception e)
            {
                _log($"{item.who}: failed ({e.Message})");
            }
        }
        return true;
    }

    public void Dispose() => _queue.CompleteAdding();
}
