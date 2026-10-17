using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace GameController;

/// <summary>%APPDATA%\upmystream-game-controller\config.json. The API key is encrypted with DPAPI: only this Windows user can read it.</summary>
[DataContract]
sealed class Config
{
    public const string DefaultHotkey = "Ctrl+Shift+F12";
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "upmystream-game-controller");
    static readonly string FilePath = Path.Combine(Dir, "config.json");

    [DataMember] public string Server = RedeemStream.DefaultServer;
    [DataMember] public string ApiKeyProtected = "";
    [DataMember] public string Game = "";
    [DataMember] public bool Chat;
    [DataMember] public string Hotkey = DefaultHotkey;
    [DataMember] public List<Trigger> Triggers = new List<Trigger>();
    [DataMember(EmitDefaultValue = false)] public List<Mapping> Mappings; // v1, read once

    public string ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(ApiKeyProtected)) return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception)
            {
                return ""; // copied from another user or PC: ask for the key again
            }
        }
        set => ApiKeyProtected = string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public static Config Load()
    {
        Config c;
        try
        {
            c = File.Exists(FilePath) ? Json.Read<Config>(File.ReadAllText(FilePath)) : new Config();
        }
        catch (Exception)
        {
            File.Copy(FilePath, FilePath + ".broken", true); // keep it for a look, start fresh
            c = new Config();
        }
        // The JSON reader skips field initializers: fill what an older file did not have.
        c.Server = string.IsNullOrWhiteSpace(c.Server) ? RedeemStream.DefaultServer : c.Server;
        c.ApiKeyProtected = c.ApiKeyProtected ?? "";
        c.Game = c.Game ?? "";
        c.Hotkey = string.IsNullOrWhiteSpace(c.Hotkey) ? DefaultHotkey : c.Hotkey;
        if (c.Triggers == null) c.Triggers = (c.Mappings ?? new List<Mapping>()).Select(Trigger.FromMapping).ToList();
        c.Mappings = null;
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Json.Write(this));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return;
            }
            catch (IOException) when (attempt < 5) // antivirus or a back-to-back save holds the file for a moment
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }
}
