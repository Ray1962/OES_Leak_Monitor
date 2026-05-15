using System.Collections.Generic;
using System.Text.Json.Serialization;
using Aqst.OesApp.Core;

namespace OES_Leak_Monitor;

public sealed class AppSettings : IJsonOnDeserialized
{
    public string Version { get; set; } = "1.0";

    public List<DeviceSettings> Devices { get; set; } = new() { new(), new() };

    public LoggerSettings Logger  { get; set; } = new();
    public AccessControlConfig AccessControl { get; set; } = new();

    // Pre-list schema kept for one-shot migration from v0.x settings.json.
    // Deserializer fills these; OnDeserialized folds them into Devices and nulls them
    // so the next Save emits only the new `devices` array.
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceSettings? Device1 { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceSettings? Device2 { get; set; }

    public void OnDeserialized()
    {
        Devices ??= new();
        if (Device1 is not null) { EnsureAt(0); Devices[0] = Device1; Device1 = null; }
        if (Device2 is not null) { EnsureAt(1); Devices[1] = Device2; Device2 = null; }
        while (Devices.Count < 2) Devices.Add(new());
    }

    private void EnsureAt(int idx)
    {
        while (Devices.Count <= idx) Devices.Add(new());
    }
}
