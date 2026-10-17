using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace GameController;

static class Program
{
    public const string WebView2Version = "1.0.2903.40"; // keep in step with GameController.csproj
    public static readonly string LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "upmystream-game-controller");

    [STAThread]
    static void Main(string[] args)
    {
        // The WebView2 DLLs live inside the exe; this must be in place before any WebView2 type is touched.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("wv2/" + new AssemblyName(e.Name).Name + ".dll"))
            {
                if (s == null) return null;
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                return Assembly.Load(bytes);
            }
        };
        Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(string[] args)
    {
        using (var single = new Mutex(true, @"Local\upmystream-game-controller", out var first))
        {
            if (!first)
            {
                MessageBox.Show("Game Controller is already running.", "upmy.stream Game Controller");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "upmy.stream Game Controller error");
            if (!WebView2Ready()) return;
            var config = Config.Load();
            // --server URL points at a test server without saving it (development only).
            var i = Array.IndexOf(args, "--server");
            var server = i >= 0 && i + 1 < args.Length ? args[i + 1] : config.Server;
            Application.Run(new MainForm(config, server, Array.IndexOf(args, "--devtools") >= 0));
        }
    }

    /// <summary>Puts the native WebView2 loader next to us once, then checks Windows has the WebView2 runtime.</summary>
    static bool WebView2Ready()
    {
        var arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
        arch = arch == "ARM64" ? "arm64" : Environment.Is64BitProcess ? "x64" : "x86";
        var dir = Path.Combine(LocalDir, "bin", WebView2Version, arch);
        var dll = Path.Combine(dir, "WebView2Loader.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(dir);
            using (var s = typeof(Program).Assembly.GetManifestResourceStream($"wv2/{arch}/WebView2Loader.dll"))
            using (var f = File.Create(dll + ".tmp"))
                s.CopyTo(f);
            File.Move(dll + ".tmp", dll);
        }
        CoreWebView2Environment.SetLoaderDllFolderPath(dir);
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (MessageBox.Show("Game Controller needs Microsoft Edge WebView2, which isn't installed on this PC.\n\nOpen the download page?",
                    "upmy.stream Game Controller", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("https://developer.microsoft.com/microsoft-edge/webview2/");
            return false;
        }
    }
}
