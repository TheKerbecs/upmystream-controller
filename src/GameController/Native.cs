using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameController;

static class Native
{
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_WHEEL = 0x800;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    const uint MAPVK_VK_TO_VSC_EX = 4;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi; // largest member: sets the union's size
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar (Windows 10 20H1+), in the app's background color on Windows 11.</summary>
    public static void DarkTitleBar(IntPtr hwnd)
    {
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var color = 0x00292622; // #222629 as 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 35, ref color, 4); // DWMWA_CAPTION_COLOR, ignored before Windows 11
    }

    public static void Run(ActionPlan p)
    {
        switch (p.Kind)
        {
            case Trigger.KeyAction:
                Press(p.Combo, p.HoldMs);
                break;
            case Trigger.MouseAction:
                Click(p.Combo, p.Button, p.HoldMs);
                break;
            case Trigger.ScrollAction: // one event per notch: some games count events, not the sum
                for (var i = 0; i < Math.Abs(p.Notches); i++)
                {
                    if (i > 0) Thread.Sleep(15);
                    Mouse(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(Math.Sign(p.Notches) * 120)));
                }
                break;
            case Trigger.MoveAction:
                Move(p.Dx, p.Dy, p.MoveMs);
                break;
        }
    }

    static void Click(Combo mods, string button, int holdMs)
    {
        uint down, up, data = 0;
        switch (button)
        {
            case "Right": down = 0x8; up = 0x10; break;
            case "Middle": down = 0x20; up = 0x40; break;
            case "Back": down = 0x80; up = 0x100; data = 1; break; // XBUTTON1
            case "Forward": down = 0x80; up = 0x100; data = 2; break; // XBUTTON2
            default: down = 0x2; up = 0x4; break;
        }
        var held = mods?.Modifiers ?? new int[0];
        foreach (var m in held) Send(m, false);
        if (held.Count > 0) Thread.Sleep(15);
        Mouse(down, 0, 0, data);
        Thread.Sleep(holdMs);
        Mouse(up, 0, 0, data);
        if (held.Count > 0) Thread.Sleep(15);
        for (var i = held.Count - 1; i >= 0; i--) Send(held[i], true);
    }

    /// <summary>Relative moves on the eased step plan, timed with a 1 ms system timer.</summary>
    static void Move(int dx, int dy, int ms)
    {
        var steps = MouseSteps.Plan(dx, dy, ms);
        timeBeginPeriod(1);
        try
        {
            var clock = Stopwatch.StartNew();
            foreach (var s in steps)
            {
                while (clock.ElapsedMilliseconds < s.AtMs) Thread.Sleep(1);
                if (s.Dx != 0 || s.Dy != 0) Mouse(MOUSEEVENTF_MOVE, s.Dx, s.Dy, 0);
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    static void Mouse(uint flags, int dx, int dy, uint data)
    {
        var mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags };
        var input = new[] { new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mi } } };
        if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Scan codes, not virtual keys: games reading DirectInput or raw input only see scan codes.
    /// Modifiers go down first and come up last, with a short gap so games that poll see them held.</summary>
    static void Press(Combo combo, int holdMs)
    {
        foreach (var m in combo.Modifiers) Send(m, false);
        if (combo.Modifiers.Count > 0) Thread.Sleep(15);
        Send(combo.Key, false);
        Thread.Sleep(holdMs);
        Send(combo.Key, true);
        if (combo.Modifiers.Count > 0) Thread.Sleep(15);
        for (var i = combo.Modifiers.Count - 1; i >= 0; i--) Send(combo.Modifiers[i], true);
    }

    static void Send(int vk, bool up)
    {
        var sc = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
        var ki = new KEYBDINPUT();
        if (sc == 0)
        {
            ki.wVk = (ushort)vk;
        }
        else
        {
            ki.wScan = (ushort)(sc & 0xFF);
            ki.dwFlags = KEYEVENTF_SCANCODE;
            if ((sc & 0xFF00) == 0xE000 || Combo.IsExtended(vk)) ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
        }
        if (up) ki.dwFlags |= KEYEVENTF_KEYUP;
        var input = new[] { new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = ki } } };
        if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>True when no game is set, or the foreground window belongs to that process.</summary>
    public static bool GameFocused(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return true;
        return string.Equals(ForegroundProcess(), StripExe(game), StringComparison.OrdinalIgnoreCase);
    }

    public static string ForegroundProcess()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string StripExe(string name)
    {
        name = (name ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
    }
}
