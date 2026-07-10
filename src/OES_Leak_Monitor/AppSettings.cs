using System.Collections.Generic;
using System.Text.Json.Serialization;
using Aqst.OesApp.Core;

namespace OES_Leak_Monitor;

public sealed class AppSettings : IJsonOnDeserialized
{
    public string Version { get; set; } = "1.0";

    public List<DeviceSettings> Devices { get; set; } = new() { new(), new() };

    // TriggerWavelength defaults to the N2 337.1 nm band head for a fresh settings.json —
    // kept to one decimal place (the line is at 337.1, not a round 337); overrides the
    // Aqst.OesApp.Core LoggerSettings default of 387 nm.
    public LoggerSettings Logger  { get; set; } = new() { TriggerWavelength = 337.1f };
    public AccessControlConfig AccessControl { get; set; } = new();

    /// <summary>Actinometry leak-monitoring model configuration and Golden Run baselines.</summary>
    public LeakMonitorSettings LeakMonitor { get; set; } = LeakMonitorSettings.CreateDefault();

    /// <summary>
    /// Optional full-spectrum CSV played back as the spectrum stream while in Test Mode
    /// (no spectrometer attached). Null/empty → use the built-in synthetic generator.
    /// Persisted so the last-used simulation file is reused on the next launch.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SimulationCsvPath { get; set; }

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

        // A settings.json predating the leak monitor has no `leakMonitor` key; an older
        // one may have the section but an empty ratio list. Either way, fold in the
        // factory ratios so the monitor always has something to compute.
        LeakMonitor ??= LeakMonitorSettings.CreateDefault();
        if (LeakMonitor.Ratios is null || LeakMonitor.Ratios.Count == 0)
            LeakMonitor.Ratios = LeakMonitorSettings.CreateDefault().Ratios;
        LeakMonitor.GoldenRuns ??= new();
        LeakMonitor.WavelengthCorrections ??= new();
    }

    private void EnsureAt(int idx)
    {
        while (Devices.Count <= idx) Devices.Add(new());
    }
}
