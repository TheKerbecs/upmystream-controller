using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GameController;

/// <summary>One event from GET /api/v1/redeems/stream. Servers before chat support send no type: a redeem.</summary>
[DataContract]
public sealed class StreamEvent
{
    [DataMember(Name = "type")] public string Type;
    [DataMember(Name = "username")] public string Username;
    [DataMember(Name = "reward_name")] public string RedeemName; // the API keeps the old name
    [DataMember(Name = "user_input")] public string UserInput; // shown in the log only, never typed
    [DataMember(Name = "cost")] public long? Cost;
    [DataMember(Name = "platform")] public string Platform;
    [DataMember(Name = "message")] public string Message;

    public bool IsChat => Type == "chat";
}

[DataContract]
public sealed class RedeemInfo
{
    [DataMember(Name = "name")] public string Name;
    [DataMember(Name = "cost")] public long? Cost;
    [DataMember(Name = "enabled")] public bool? Enabled;
}

/// <summary>v1 config rows; read once and turned into triggers.</summary>
[DataContract]
public sealed class Mapping
{
    [DataMember] public bool Enabled = true;
    [DataMember] public string Reward = "";
    [DataMember] public string Keys = "";
    [DataMember] public int HoldMs = 50;
    [DataMember] public int CooldownS;
}

public static class Json
{
    public static T Read<T>(string text)
    {
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s);
    }

    public static string Write<T>(T value)
    {
        using (var s = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(T)).WriteObject(s, value);
            return Encoding.UTF8.GetString(s.ToArray());
        }
    }
}
