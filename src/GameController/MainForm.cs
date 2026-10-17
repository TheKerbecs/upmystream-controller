using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GameController;

/// <summary>Hosts the page (ui/) in WebView2 and owns everything behind it: config, the stream, input, tray,
/// the on/off hotkey. The page sends UiRequests and gets the whole UiState back after every change.</summary>
sealed class MainForm : Form
{
    const string Origin = "https://app.game-controller/";
    const int HotkeyId = 1, LogMax = 100;
    static readonly Color Background = Color.FromArgb(0x22, 0x26, 0x29);

    readonly Config _config;
    readonly string _server;
    readonly bool _devtools;
    readonly Dispatcher _dispatcher;
    WebView2 _web; // only while the window shows: hidden in the tray, the browser processes would cost ~340 MB
    readonly List<string> _log = new List<string>();
    readonly Dictionary<string, string> _resources;
    List<string> _redeems = new List<string>();
    List<string> _games = new List<string>();
    CancellationTokenSource _run;
    string _status = "stopped", _detail = "";
    volatile string _game;
    bool _capturing, _ready, _hotkeyOn, _redeemsLoaded;

    public MainForm(Config config, string server, bool devtools)
    {
        _config = config;
        _server = server;
        _devtools = devtools;
        _game = config.Game;
        _dispatcher = new Dispatcher(Native.Run, () => Native.GameFocused(_game), msg => Ui(() => Log(msg)))
        {
            ChatEnabled = config.Chat,
        };
        _dispatcher.SetTriggers(config.Triggers);
        _resources = typeof(MainForm).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("ui"))
            .ToDictionary(n => n.Replace('\\', '/').Substring(3), n => n, StringComparer.OrdinalIgnoreCase);

        Text = "upmy.stream Game Controller";
        Icon = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"));
        BackColor = Background;
        Size = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(860));
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(560));
        StartPosition = FormStartPosition.CenterScreen;

        // Minimized, the page and its browser processes (~300 MB) are let go; restoring builds them again.
        Resize += (_, __) =>
        {
            if (WindowState == FormWindowState.Minimized) ReleaseWeb();
            else if (IsHandleCreated && Visible) _ = InitWeb(); // not while the constructor sets the size
        };

        FillGames();
        if (_server != RedeemStream.DefaultServer) Log("Server: " + _server);
        Load += async (_, __) => await InitWeb();
    }


    // ---- page ----

    async Task InitWeb()
    {
        if (_web != null) return;
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
        Controls.Add(_web);
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.LocalDir, "webview"));
        var web = _web;
        try
        {
            await web.EnsureCoreWebView2Async(env);
        }
        catch (ObjectDisposedException)
        {
            return; // hidden again while starting
        }
        if (web != _web) return;
        var core = web.CoreWebView2;
        var s = core.Settings;
        s.AreDevToolsEnabled = _devtools;
        s.AreDefaultContextMenusEnabled = _devtools;
        s.AreBrowserAcceleratorKeysEnabled = false; // no F5, Ctrl+P, Ctrl+F in an app window
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = Serve(env, e.Request.Uri);
        core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith(Origin); // the page never leaves the app
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        core.Navigate(Origin + "index.html");
    }

    /// <summary>The page's files come from inside the exe; nothing is fetched from the network.</summary>
    CoreWebView2WebResourceResponse Serve(CoreWebView2Environment env, string uri)
    {
        var path = new Uri(uri).AbsolutePath.TrimStart('/');
        if (!_resources.TryGetValue(path, out var name)) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream",
        };
        var data = new MemoryStream();
        using (var res = typeof(MainForm).Assembly.GetManifestResourceStream(name)) res.CopyTo(data);
        data.Position = 0;
        return env.CreateWebResourceResponse(data, 200, "OK", "Content-Type: " + type + "\r\nCache-Control: no-store");
    }

    void OnMessage(string json)
    {
        UiRequest r;
        try
        {
            r = Json.Read<UiRequest>(json);
        }
        catch (Exception)
        {
            return;
        }
        switch (r.Type)
        {
            case "ready":
                _ready = true;
                if (_config.ApiKey.Length > 0 && _run == null) Start();
                break;
            case "set-key":
                _ = SetKey((r.Text ?? "").Trim());
                return;
            case "refresh-redeems":
                _ = RefreshRedeems(true);
                return;
            case "list-games":
                FillGames();
                break;
            case "set-game":
                _game = _config.Game = Native.StripExe(r.Text);
                Save();
                break;
            case "save-trigger":
                SaveTrigger(r.Trigger);
                break;
            case "delete-trigger":
                foreach (var gone in _config.Triggers.Where(t => t.Id == r.Id).ToList())
                {
                    _config.Triggers.Remove(gone);
                    Log($"Deleted trigger \"{gone.Match}\"");
                }
                ApplyTriggers();
                break;
            case "set-chat":
                _config.Chat = _dispatcher.ChatEnabled = r.On;
                Save();
                Log(r.On ? "Chat triggers turned on" : "Chat triggers turned off");
                if (_run != null) Start(); // reconnect: the server only sends chat when asked
                break;
            case "set-hotkey":
                SetHotkey(r.Text);
                break;
            case "start":
                Start();
                break;
            case "stop":
                Stop();
                break;
            case "pause":
                SetPaused(r.On, false);
                break;
            case "check-keys":
                var ok = Combo.TryParse(r.Text, out var combo, out var error);
                Reply("keys-checked", ok, ok ? combo.ToString() : r.Text, error);
                return;
            case "capture": // listening for keys: the hotkey must not fire, Alt+F4 must not close
                _capturing = r.On;
                if (_capturing) UnregisterHotkey();
                else RegisterHotkey(_config.Hotkey);
                return;
            case "run-now":
                TryNow(r.Trigger);
                break;
        }
        PushState();
    }

    void PushState()
    {
        if (!_ready || _web?.CoreWebView2 == null) return;
        var key = _config.ApiKey;
        var state = new UiState
        {
            Version = Application.ProductVersion,
            KeyHint = key.Length == 0 ? "" : "••••" + key.Substring(Math.Max(0, key.Length - 4)),
            Status = _status,
            Detail = _detail ?? "",
            Running = _run != null,
            Paused = _dispatcher.Paused,
            Chat = _config.Chat,
            Hotkey = _config.Hotkey,
            Game = _config.Game,
            Games = _games,
            Redeems = _redeems,
            Triggers = _config.Triggers.Select(View).ToList(),
            Log = _log,
        };
        _web.CoreWebView2.PostWebMessageAsJson(Json.Write(state));
    }

    TriggerView View(Trigger t)
    {
        var v = TriggerView.Of(t);
        if (_redeemsLoaded && t.Source == Trigger.Redeem && !string.IsNullOrWhiteSpace(t.Match)
            && !_redeems.Any(r => string.Equals(r.Trim(), t.Match.Trim(), StringComparison.OrdinalIgnoreCase)))
            v.Problem = "deleted on upmy.stream";
        return v;
    }

    void Reply(string type, bool ok, string text, string error) =>
        _web?.CoreWebView2?.PostWebMessageAsJson(Json.Write(new UiReply { Type = type, Ok = ok, Text = text ?? "", Error = error ?? "" }));

    // ---- key, redeems, triggers ----

    async Task SetKey(string key)
    {
        if (key.Length == 0) return;
        try
        {
            var redeems = await RedeemStream.Redeems(_server, key);
            _config.ApiKey = key;
            Save();
            Reply("key-test", true, $"Connected. Found {redeems.Count} redeem{(redeems.Count == 1 ? "" : "s")}.", null);
            FillRedeems(redeems);
            Start();
        }
        catch (Exception e)
        {
            Reply("key-test", false, null, e.Message);
        }
        PushState();
    }

    async Task RefreshRedeems(bool asked = false)
    {
        try
        {
            FillRedeems(await RedeemStream.Redeems(_server, _config.ApiKey), asked);
            PushState();
        }
        catch (Exception e)
        {
            Log("Couldn't load your redeems: " + e.Message);
        }
    }

    /// <summary>Every redeem on upmy.stream shows up as a trigger, off until it gets an action. A redeem deleted there
    /// takes its trigger along if that was never set up; a set-up one stays, flagged, for the user to delete.</summary>
    void FillRedeems(List<RedeemInfo> redeems, bool asked = false)
    {
        _redeems = redeems.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        _redeemsLoaded = true;
        var live = new HashSet<string>(_redeems.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var removed = _config.Triggers.RemoveAll(t => t.Source == Trigger.Redeem && !live.Contains((t.Match ?? "").Trim())
            && !t.Enabled && t.Action == Trigger.KeyAction && string.IsNullOrEmpty(t.Keys));
        var have = new HashSet<string>(_config.Triggers.Where(t => t.Source == Trigger.Redeem).Select(t => t.Match.Trim()), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var name in _redeems.Where(n => !have.Contains(n.Trim())))
        {
            _config.Triggers.Add(new Trigger { Source = Trigger.Redeem, Match = name, Enabled = false });
            added++;
        }
        if (added > 0) Log($"Added {added} new redeem{(added == 1 ? "" : "s")} from upmy.stream. Set what {(added == 1 ? "it does" : "they do")} and turn {(added == 1 ? "it" : "them")} on.");
        if (removed > 0) Log($"Removed {removed} redeem{(removed == 1 ? "" : "s")} that {(removed == 1 ? "was" : "were")} deleted on upmy.stream.");
        if (added + removed == 0)
        {
            if (asked) Log($"Redeems are up to date ({_redeems.Count} on upmy.stream).");
            return;
        }
        ApplyTriggers();
    }

    void SaveTrigger(Trigger t)
    {
        if (t == null) return;
        if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var i = _config.Triggers.FindIndex(x => x.Id == t.Id);
        if (i >= 0) _config.Triggers[i] = t;
        else _config.Triggers.Add(t);
        ApplyTriggers();
    }

    void ApplyTriggers()
    {
        _dispatcher.SetTriggers(_config.Triggers);
        Save();
    }

    /// <summary>"Try it": runs once right away (the countdown ran in the page), if the game is in front by now.</summary>
    void TryNow(Trigger t)
    {
        ActionPlan plan = null;
        string error = "nothing to run";
        if (t == null || !t.TryPlan(out plan, out error))
        {
            Log("Try it: " + error);
            return;
        }
        Task.Run(() =>
        {
            if (!Native.GameFocused(_game))
            {
                Ui(() => Log($"Try it: {_game} wasn't the active window, so nothing happened."));
                return;
            }
            try
            {
                Native.Run(plan);
                Ui(() => Log("Try it: " + plan));
            }
            catch (Exception e)
            {
                Ui(() => Log("Try it failed: " + e.Message));
            }
        });
    }

    void Save()
    {
        try
        {
            _config.Save();
        }
        catch (Exception e)
        {
            Log("Couldn't save settings: " + e.Message);
        }
    }

    void FillGames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var self = Process.GetCurrentProcess().ProcessName;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.ProcessName != self && p.MainWindowTitle.Length > 0) names.Add(p.ProcessName);
            }
            catch (Exception)
            {
                // exited or not ours to inspect
            }
            finally
            {
                p.Dispose();
            }
        }
        _games = names.ToList();
    }

    // ---- connection ----

    void Start()
    {
        var key = _config.ApiKey;
        if (key.Length == 0) return;
        _run?.Cancel();
        var run = _run = new CancellationTokenSource();
        var stream = new RedeemStream(_server, key, _config.Chat, _dispatcher.Handle,
            (state, msg) => Ui(() => { if (_run == run) OnState(state, msg); })); // ignore a replaced stream winding down
        Task.Run(() => stream.Run(run.Token));
        _ = RefreshRedeems();
    }

    void Stop()
    {
        _run?.Cancel();
        _run = null;
        _status = "stopped";
        _detail = "";
        Log("Stopped");
    }

    void OnState(StreamState state, string detail)
    {
        switch (state)
        {
            case StreamState.Connecting:
                _status = "connecting";
                break;
            case StreamState.Connected:
                if (_status != "connected") Log("Connected");
                _status = "connected";
                _detail = "";
                break;
            case StreamState.Reconnecting:
                _status = "reconnecting";
                _detail = detail ?? "";
                if (detail != null) Log($"Connection lost ({detail}), reconnecting");
                break;
            case StreamState.BadKey:
                _run = null;
                _status = "badkey";
                _detail = "Check your key on upmy.stream under API Keys";
                Log("Your API key was rejected. Check it on upmy.stream under API Keys.");
                ShowWindow();
                break;
            case StreamState.Stopped:
                return;
        }
        PushState();
    }

    // ---- on/off ----

    void SetPaused(bool paused, bool fromHotkey)
    {
        if (_dispatcher.Paused == paused) return;
        _dispatcher.Paused = paused;
        if (fromHotkey) (paused ? SystemSounds.Hand : SystemSounds.Asterisk).Play(); // audible in-game
        Log(paused ? "Paused. Nothing is sent until you turn it back on." : "Resumed.");
        PushState();
    }

    void SetHotkey(string text)
    {
        if (!Combo.TryParseHotkey(text, out _, out _, out var error))
        {
            Reply("hotkey-set", false, null, char.ToUpper(error[0]) + error.Substring(1)); // checked before RegisterHotKey
            return;
        }
        var canonical = string.Join("+", (text ?? "").Split('+').Select(t => t.Trim()));
        UnregisterHotkey();
        if (!RegisterHotkey(canonical))
        {
            RegisterHotkey(_config.Hotkey);
            Reply("hotkey-set", false, null, canonical + " is already used by another app");
            return;
        }
        _config.Hotkey = canonical;
        Save();
        Log("On/off hotkey set to " + canonical);
        Reply("hotkey-set", true, canonical, null);
    }

    bool RegisterHotkey(string text)
    {
        if (_hotkeyOn || !IsHandleCreated || !Combo.TryParseHotkey(text, out var mods, out var vk, out _)) return false;
        _hotkeyOn = Native.RegisterHotKey(Handle, HotkeyId, mods | Native.MOD_NOREPEAT, (uint)vk);
        return _hotkeyOn;
    }

    void UnregisterHotkey()
    {
        if (_hotkeyOn) Native.UnregisterHotKey(Handle, HotkeyId);
        _hotkeyOn = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == HotkeyId) SetPaused(!_dispatcher.Paused, true);
        base.WndProc(ref m);
    }

    // ---- window, tray ----

    void Log(string msg)
    {
        _log.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "   " + msg);
        if (_log.Count > LogMax) _log.RemoveAt(_log.Count - 1);
        PushState();
    }

    void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(a);
        }
        catch (InvalidOperationException)
        {
            // closing
        }
    }

    void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _ = InitWeb();
    }

    void ReleaseWeb()
    {
        _ready = false;
        var web = _web;
        _web = null;
        if (web == null) return;
        Controls.Remove(web);
        web.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
        if (!RegisterHotkey(_config.Hotkey))
            Log(_config.Hotkey + " is already used by another app. Pick a different hotkey in Setup or use the On/Off button.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_capturing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // Alt+F4 while setting keys: the page never sees the F4, so tell it
            Reply("keys-checked", false, "Alt+F4", Combo.NotAllowed);
            return;
        }
        Save();
        _run?.Cancel();
        _dispatcher.Dispose();
        UnregisterHotkey();
        base.OnFormClosing(e);
    }
}
