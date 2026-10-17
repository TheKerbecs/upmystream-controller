using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameController;

static class Program
{
    static int _failed;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    static int Main()
    {
        Combos();
        Sse();
        Dispatch();
        Triggers();
        Mouse();
        Stream().GetAwaiter().GetResult();
        Console.WriteLine(_failed == 0 ? "all passed" : $"{_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    static string Parse(string s) => Combo.TryParse(s, out var c, out var e) ? c.ToString() : "!" + e;

    static void Combos()
    {
        Check(Parse("shift+q") == "Shift+Q", "parse is case-insensitive and canonical");
        Check(Parse("Q + Alt + ctrl") == "Ctrl+Alt+Q", "modifiers sort Ctrl, Shift, Alt");
        Check(Parse("Shift") == "Shift", "a modifier alone is a key");
        Check(Parse("ctrl+shift") == "Ctrl+Shift", "modifiers only: last is the key");
        Check(Parse("F12") == "F12" && Parse("num5") == "Num5" && Parse("space") == "Space", "named keys");
        Check(Parse("Q+W").StartsWith("!use only one"), "two main keys rejected");
        Check(Parse("Win+R").StartsWith("!unknown"), "Win cannot be mapped");
        Check(Parse("").StartsWith("!no key"), "empty rejected");
        foreach (var bad in new[] { "Alt+F4", "Ctrl+Alt+F4", "Alt+Tab", "Ctrl+Esc", "Ctrl+Shift+Esc", "Alt+Esc", "Ctrl+Alt+Delete" })
            Check(Parse(bad) == "!" + Combo.NotAllowed, $"{bad} blocked");
        Check(Parse("Esc") == "Esc" && Parse("F4") == "F4" && Parse("Shift+Tab") == "Shift+Tab", "harmless neighbours allowed");
        Check(Combo.NameOf(0x51) == "Q" && Combo.NameOf(0x5B) == null, "NameOf: Q yes, LWin no");
        Check(new[] { "Left", "Up", "Right", "Down", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "NumDivide" }
            .All(k => Combo.TryParse(k, out var c, out _) && Combo.IsExtended(c.Key)), "arrow block is extended");
        Check(new[] { "Num4", "Num8", "Q", "Space", "Enter", "Shift", "NumAdd" }
            .All(k => Combo.TryParse(k, out var c, out _) && !Combo.IsExtended(c.Key)), "numpad and plain keys are not");
    }

    static void Sse()
    {
        var p = new SseParser();
        var got = new List<SseEvent>();
        foreach (var line in new[] { ": ping", "", "data: {\"a\":1}", "", "event: redeem", "data: x", "data:y", "", "data: dangling" })
        {
            var ev = p.Line(line);
            if (ev != null) got.Add(ev);
        }
        Check(got.Count == 2, "comments and blank lines alone emit nothing; unfinished event waits");
        Check(got[0].Name == "message" && got[0].Data == "{\"a\":1}", "default event name is message");
        Check(got[1].Name == "redeem" && got[1].Data == "x\ny", "event name + multi-line data");
    }

    static void Dispatch()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var ran = new List<string>();
        var logs = new List<string>();
        var focused = true;
        var d = new Dispatcher(p => ran.Add(p.ToString()), () => focused, logs.Add, () => now, startWorker: false);
        d.SetTriggers(new[]
        {
            new Trigger { Match = "Fireball", Keys = "shift+q", HoldMs = 80, CooldownS = 10 },
            new Trigger { Match = "Jump", Keys = "Space", HoldMs = 999999 },
            new Trigger { Match = "Off", Keys = "E", Enabled = false },
            new Trigger { Match = "Broken", Keys = "Alt+F4" },
            new Trigger { Source = Trigger.Chat, Match = "w", Keys = "W" },
            new Trigger { Source = Trigger.Chat, Match = " Super ", Action = Trigger.MouseAction, Button = "Right", Keys = "Shift" },
        });
        StreamEvent Ev(string redeem, string text = "") => new StreamEvent { Username = "viewer", RedeemName = redeem, UserInput = text };
        StreamEvent Chat(string msg) => new StreamEvent { Type = "chat", Username = "viewer", Message = msg };

        d.Handle(Ev("fireball", "Win+R cmd"));
        d.RunOne(0);
        Check(ran.SequenceEqual(new[] { "Shift+Q · 80 ms" }), "mapped redeem runs, name case-insensitive, viewer text ignored");

        d.Handle(Ev("Fireball"));
        Check(d.RunOne(0) == false && logs.Last().Contains("cooldown"), "cooldown blocks a repeat");
        now = now.AddSeconds(11);
        d.Handle(Ev("Fireball"));
        d.RunOne(0);
        Check(ran.Count == 2, "cooldown over: runs again");

        d.Handle(Ev("Jump"));
        d.RunOne(0);
        Check(ran.Last() == $"Space · {Trigger.HoldMax} ms", "hold clamped to max");

        d.Handle(Ev("Off"));
        Check(logs.Last().Contains("no trigger"), "disabled trigger ignored");
        d.Handle(Ev("Broken"));
        Check(logs.Last().Contains(Combo.NotAllowed), "blocked combo in config never pressed");

        var before = logs.Count;
        d.Handle(Chat("w"));
        Check(d.RunOne(0) == false && logs.Count == before, "chat off: chat lines ignored silently");
        d.ChatEnabled = true;
        d.Handle(Chat(" W "));
        d.RunOne(0);
        Check(ran.Last() == "W · 50 ms", "chat on: exact message fires, case and spaces ignored");
        d.Handle(Chat("super"));
        d.RunOne(0);
        Check(ran.Last() == "Shift+Right click · 50 ms", "chat trigger with a mouse action");
        before = logs.Count;
        d.Handle(Chat("wow"));
        d.Handle(Chat("w lol"));
        d.Handle(Ev("w"));
        Check(d.RunOne(0) == false && logs.Count == before + 1 && logs.Last().Contains("no trigger"),
            "no partial chat match; chat triggers never match redeems; other chat stays quiet");

        for (var i = 0; i < 8; i++) d.Handle(Ev("Jump"));
        Check(logs.Count(l => l.Contains("dropped")) == 3, "queue holds 5, rest dropped");
        before = ran.Count;
        while (d.RunOne(0)) { }
        Check(ran.Count == before + 5, "queued actions all run");

        focused = false;
        d.Handle(Ev("Jump"));
        d.RunOne(0);
        Check(logs.Last().Contains("game not active") && ran.Count == before + 5, "game not in front: skipped");
        focused = true;

        d.Paused = true;
        d.Handle(Ev("Jump"));
        Check(logs.Last().Contains("paused") && d.RunOne(0) == false, "paused: not queued");
        d.Paused = false;
        d.Handle(Ev("Jump"));
        d.Paused = true;
        d.RunOne(0);
        Check(logs.Last().Contains("paused") && ran.Count == before + 5, "paused after queueing: still skipped");
        d.Dispose();
        d.Handle(Ev("Jump"));
        Check(d.RunOne(0) == false, "disposed: no more actions, no throw");
    }

    static string Plan(Trigger t) => t.TryPlan(out var p, out var e) ? p.ToString() : "!" + e;

    static void Triggers()
    {
        Check(Plan(new Trigger { Action = "mouse", Button = "Left" }) == "Left click · 50 ms", "left click");
        Check(Plan(new Trigger { Action = "mouse", Button = "Back", Keys = "ctrl+shift" }) == "Ctrl+Shift+Back click · 50 ms", "click with modifiers");
        Check(Plan(new Trigger { Action = "mouse", Button = "Left", Keys = "Q" }).StartsWith("!only Ctrl"), "click with a letter rejected");
        Check(Plan(new Trigger { Action = "mouse", Button = "Thumb" }).StartsWith("!pick"), "unknown button rejected");
        Check(Plan(new Trigger { Action = "scroll", Notches = 3 }) == "Scroll up ×3" && Plan(new Trigger { Action = "scroll", Notches = -99 }) == "Scroll down ×20", "scroll, clamped");
        Check(Plan(new Trigger { Action = "scroll", Notches = 0 }).StartsWith("!scroll"), "zero scroll rejected");
        Check(Plan(new Trigger { Action = "move", Dx = -500, MoveMs = 300 }) == "Move -500, 0 · 300 ms", "move");
        Check(Plan(new Trigger { Action = "move", Dx = 99999, Dy = -99999, MoveMs = 99999 }) == "Move 5000, -5000 · 5000 ms", "move clamped");
        Check(Plan(new Trigger { Action = "move" }).StartsWith("!set a distance"), "zero move rejected");
        Check(Plan(new Trigger { Action = "dance" }).StartsWith("!pick an action"), "unknown action rejected");
        Check(new Trigger().Source == "redeem" && new Trigger().Enabled && new Trigger().Id.Length == 8, "new trigger defaults");

        var old = Json.Read<Trigger>("{\"match\":\"jump\",\"keys\":\"Space\"}");
        Check(old.Enabled && old.Source == "redeem" && old.Action == "key" && old.HoldMs == 50 && old.Id.Length == 8,
            "fields missing from JSON get defaults, not zero");
        var m = Trigger.FromMapping(new Mapping { Reward = "Fireball", Keys = "Shift+Q", HoldMs = 80, CooldownS = 3, Enabled = false });
        Check(!m.Enabled && m.Source == "redeem" && m.Match == "Fireball" && Plan(m) == "Shift+Q · 80 ms" && m.CooldownS == 3, "v1 mapping converts");
        Check(TriggerView.Of(new Trigger { Keys = "Q" }).Problem.Contains("no redeem or chat"), "trigger without match text flagged");
        Check(TriggerView.Of(new Trigger { Match = "x" }).Problem == "no key set", "trigger without keys flagged");

        Check(Combo.TryParseHotkey("ctrl+shift+f12", out var mods, out var vk, out _) && mods == 0x6 && vk == 0x7B, "hotkey parse");
        Check(!Combo.TryParseHotkey("F12", out _, out _, out _) && !Combo.TryParseHotkey("Ctrl+Shift", out _, out _, out _)
            && !Combo.TryParseHotkey("Ctrl+A+B", out _, out _, out _), "hotkey needs a modifier plus exactly one key");
        foreach (var bad in new[] { "Alt+F4", "Alt+Tab", "Ctrl+Esc", "Ctrl+Shift+Esc", "Alt+Esc", "Ctrl+Alt+Delete" })
        {
            Combo.TryParseHotkey(bad, out _, out _, out var why);
            Check(why == Combo.NotAllowed, $"hotkey {bad} blocked before registering");
        }

        var req = Json.Read<UiRequest>("{\"type\":\"save-trigger\",\"trigger\":{\"id\":\"ab\",\"source\":\"chat\",\"match\":\"w\",\"action\":\"move\",\"dx\":-5}}");
        Check(req.Type == "save-trigger" && req.Trigger.Id == "ab" && req.Trigger.Dx == -5 && req.Trigger.MoveMs == 200, "page message parses, defaults kept");
        Check(Json.Write(new UiState()).Contains("\"type\":\"state\""), "state serializes");
    }

    static void Mouse()
    {
        foreach (var (dx, dy, ms) in new[] { (-500, 0, 300), (37, -11, 1000), (5000, 5000, 5000), (3, 0, 300), (100, 50, 0), (1, 1, 1) })
        {
            var steps = MouseSteps.Plan(dx, dy, ms);
            Check(steps.Sum(s => s.Dx) == dx && steps.Sum(s => s.Dy) == dy, $"move {dx},{dy} over {ms} ms adds up exactly");
            Check(steps.Last().AtMs == (steps.Count == 1 ? 0 : ms) && steps.Zip(steps.Skip(1), (a, b) => b.AtMs > a.AtMs).All(x => x), $"move {dx},{dy}: steps in time order, end on time");
        }
        Check(MouseSteps.Plan(100, 50, 0).Count == 1, "0 ms: one jump");
        var s3 = MouseSteps.Plan(-500, 0, 300);
        int Moved(int fromMs, int toMs) => s3.Where(s => s.AtMs > fromMs && s.AtMs <= toMs).Sum(s => -s.Dx);
        Check(Moved(0, 60) < Moved(120, 180) && Moved(240, 300) < Moved(120, 180), "ease in-out: slow at both ends, fast in the middle");
        Check(s3.All(s => s.Dx <= 0), "never moves backwards");
    }

    // A tiny HTTP server: each accepted connection gets the next canned response.
    static async Task Stream()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var keys = new List<string>();
        var paths = new List<string>();
        var responses = new Queue<string>(new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n" +
            ": ping\n\ndata: {\"kind\":\"redeem\",\"username\":\"a\",\"reward_name\":\"Fireball\",\"user_input\":\"hi\",\"cost\":5}\n\n" +
            "data: not json\n\ndata: {\"username\":\"b\"}\n\n" +
            "data: {\"type\":\"chat\",\"platform\":\"twitch\",\"username\":\"d\",\"message\":\"w\"}\n\ndata: {\"type\":\"chat\",\"username\":\"e\"}\n\n",
            "HTTP/1.1 500 Oops\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\nevent: redeem\ndata: {\"username\":\"c\",\"reward_name\":\"Jump\"}\n\n",
            "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        });
        var server = Task.Run(async () =>
        {
            while (responses.Count > 0)
            {
                using (var client = await listener.AcceptTcpClientAsync())
                using (var s = client.GetStream())
                {
                    var buf = new byte[4096];
                    var head = Encoding.ASCII.GetString(buf, 0, await s.ReadAsync(buf, 0, buf.Length));
                    keys.Add(head.Split("\r\n").FirstOrDefault(l => l.StartsWith("X-API-Key:"))?.Substring(10).Trim());
                    paths.Add(head.Split(' ')[1]);
                    var bytes = Encoding.UTF8.GetBytes(responses.Dequeue());
                    await s.WriteAsync(bytes, 0, bytes.Length);
                }
            }
        });
        var events = new List<StreamEvent>();
        var states = new List<string>();
        var rs = new RedeemStream(url, "k123", true, events.Add, (st, msg) => states.Add($"{st}:{msg}"));
        rs.MaxBackoff = TimeSpan.FromMilliseconds(50);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var run = rs.Run(cts.Token);
            Check(await Task.WhenAny(run, Task.Delay(9000)) == run, "stream stops on its own after 401");
        }
        await server;
        Check(keys.Count == 4 && keys.All(k => k == "k123"), "every connection sends X-API-Key");
        Check(events.Select(e => e.IsChat ? "chat:" + e.Message : e.RedeemName).SequenceEqual(new[] { "Fireball", "chat:w", "Jump" }),
            "redeems and chat parsed; ping, bad json, nameless and empty chat skipped");
        Check(paths.All(p => p == "/api/v1/redeems/stream?chat=1"), "chat on: asks for ?chat=1");
        Check(events[0].Username == "a" && events[0].UserInput == "hi" && events[0].Cost == 5, "event fields");
        Check(states.Contains("Reconnecting:HTTP 500") && states.Last() == "BadKey:API key rejected", "500 retried, 401 is final");
        listener.Stop();

        // Read timeout: a server that sends headers and then goes silent.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var hold = Task.Run(async () =>
        {
            using (var c = await silent.AcceptTcpClientAsync())
            {
                var s = c.GetStream();
                var b = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n");
                await s.ReadAsync(new byte[4096], 0, 4096);
                await s.WriteAsync(b, 0, b.Length);
                await Task.Delay(3000);
            }
        });
        var st2 = new List<string>();
        var rs2 = new RedeemStream($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", "k", false, _ => { }, (st, msg) => st2.Add($"{st}:{msg}"))
            { ReadTimeout = TimeSpan.FromMilliseconds(300) };
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)))
            await rs2.Run(cts.Token);
        Check(st2.Contains("Reconnecting:no response for 45 s") && st2.Last() == "Stopped:", "silent stream times out; cancel stops");
        silent.Stop();
        await hold;
    }
}
