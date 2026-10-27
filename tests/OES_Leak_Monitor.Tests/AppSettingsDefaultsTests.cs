using System.IO;
using System.Text.Json;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The factory values this app overrides on top of the framework's <c>DeviceSettings</c> /
/// <c>LoggerSettings</c> defaults. They are one-line assignments that nothing else references,
/// which is exactly how one of them got lost: the framework ships
/// <c>DeviceSettings.ForceTestMode = true</c>, so every fresh settings.json ran the leak monitor
/// on the synthetic generator until somebody found the checkbox on the Engineer-gated
/// Configuration tab.
/// </summary>
public class AppSettingsDefaultsTests
{
    /// <summary>Mirrors <c>SettingsService.JsonOptions</c> — camelCase is what is on disk.</summary>
    private static readonly JsonSerializerOptions OnDisk = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public void FreshSettings_ConnectToHardware_NotTheSyntheticGenerator()
    {
        var settings = new AppSettings();

        Assert.All(settings.Devices, d => Assert.False(d.ForceTestMode));
    }

    [Fact]
    public void FreshSettings_ArmTheRecorderOnTheNitrogenBandHead()
    {
        var settings = new AppSettings();

        Assert.True(settings.Logger.Enabled);
        Assert.Equal(AppSettings.DefaultTriggerWavelengthNm, settings.Logger.TriggerWavelength);
    }

    /// <summary>
    /// A file predating the <c>devices</c> array (or holding a short one) is padded to two
    /// entries. The padding must carry this app's factory values too, or the migration itself
    /// re-arms test mode on the slot the app actually uses.
    /// </summary>
    [Fact]
    public void PaddedDeviceEntries_CarryTheAppsDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"devices":[]}""", OnDisk)!;

        Assert.Equal(2, settings.Devices.Count);
        Assert.All(settings.Devices, d => Assert.False(d.ForceTestMode));
    }

    /// <summary>
    /// The other half of the contract: a stored decision wins over the factory value. Someone
    /// who deliberately saved a test-mode configuration keeps it.
    /// </summary>
    [Fact]
    public void StoredForceTestMode_IsHonoured()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"devices":[{"forceTestMode":true}]}""", OnDisk)!;

        Assert.True(settings.Devices[0].ForceTestMode);
    }

    /// <summary>
    /// Aqst.OesSpectrometer 0.5.0 renamed <c>OesAcquireMode.HardwareAverage</c> to <c>HWAvg</c> with
    /// no alias. Every settings.json saved before the upgrade says "HardwareAverage"; if that fails
    /// to parse, <c>Load</c> falls back to defaults and the next Save overwrites the site's
    /// installed configuration. Goes through the real <c>SettingsService</c>, because the fallback
    /// that hides the failure lives there.
    /// </summary>
    [Fact]
    public void SettingsSavedBeforeSdk050_StillLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OesLeakSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"devices":[{"integrationTimeMs":150,"acquireMode":"HardwareAverage","averageMode":"Software"}]}""");

            var settings = new SettingsService(dir).Load();

            Assert.Equal(150f, settings.Devices[0].IntegrationTimeMs);   // not the factory 50 ms
            Assert.Equal(Aqst.OesSpectrometer.Models.OesAcquireMode.HWAvg, settings.Devices[0].AcquireMode);
            Assert.Equal(Aqst.OesSpectrometer.Models.OesAverageMode.Software, settings.Devices[0].AverageMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A settings.json written before tool events existed has a <c>secs</c> section without
    /// <c>toolEvents</c>; it must load with them off and the table empty, so an upgrade changes
    /// nothing about how the tool is judged.
    /// </summary>
    [Fact]
    public void SettingsWithoutToolEvents_LoadWithThemOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"secs":{"enabled":true,"chamberCode":3}}""", OnDisk)!;

        Assert.True(settings.Secs.Enabled);
        Assert.False(settings.Secs.ToolEvents.Enabled);
        Assert.Empty(settings.Secs.ToolEvents.Mappings);
    }

    /// <summary>
    /// A settings.json written before the EAP link existed loads with it off — an upgrade opens
    /// no connection until someone asks for one — with the address the EAP gave already filled
    /// in, and the nine ids unset, so the link cannot start until someone enters them.
    /// </summary>
    [Fact]
    public void SettingsWithoutTheEapLink_LoadWithItOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"secs":{"toolEvents":{"enabled":true}}}""", OnDisk)!;

        var eap = settings.Secs.ToolEvents.Eap;
        Assert.False(eap.Enabled);
        Assert.Equal("10.2.3.120", eap.RemoteHost);
        Assert.Equal(20347, eap.RemotePort);
        Assert.Equal(0, eap.DeviceId);
        Assert.Equal(75, eap.SilenceTimeoutSeconds);
        Assert.All(eap.StatusSvids, id => Assert.Equal(0u, id));
        Assert.Equal(0u, eap.ForwardCeid);
        Assert.NotNull(eap.IdProblem());
    }

    /// <summary>
    /// The table round-trips through the real <c>SettingsService</c> with its edges as words, the
    /// way a site writes them by hand.
    /// </summary>
    [Fact]
    public void ToolEventTable_RoundTripsThroughSettingsJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OesLeakSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
                {"secs":{"toolEvents":{"enabled":true,"mappings":[
                  {"ceid":3100439529,"className":"A","edge":"Start","name":"PM3 ProcessStart","plasmaThreshold":1200},
                  {"ceid":3100439530,"className":"A","edge":"End","name":"PM3 ProcessEnd"}]}}}
                """);

            var service = new SettingsService(dir);
            var loaded = service.Load();
            service.Save(loaded);
            var again = service.Load();

            var rows = again.Secs.ToolEvents.Mappings;
            Assert.True(again.Secs.ToolEvents.Enabled);
            Assert.Equal(2, rows.Count);
            Assert.Equal(3100439529u, rows[0].Ceid);
            Assert.Equal(ToolEventEdge.Start, rows[0].Edge);
            Assert.Equal(1200, rows[0].PlasmaThreshold);
            Assert.Equal(ToolEventEdge.End, rows[1].Edge);
            Assert.Contains("\"End\"", File.ReadAllText(Path.Combine(dir, "settings.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The SECS tab does not edit the tool-event table, but its Save writes the whole SECS
    /// section back — so its edit buffer has to carry the table, or one Save wipes it.
    /// </summary>
    [Fact]
    public void SavingTheSecsTab_KeepsTheToolEventTable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OesLeakSecsVm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var bridge = new SecsBridge(null, dir, dir, () => default);
            var original = new SecsSettings
            {
                ToolEvents = new ToolEventSettings
                {
                    Enabled = true,
                    Mappings = { new ToolEventMapping { Ceid = 3100439529, ClassName = "A" } },
                },
            };
            var vm = new SecsViewModel(bridge, original, _ => { });

            var saved = vm.ToSettings();

            Assert.True(saved.ToolEvents.Enabled);
            Assert.Equal(3100439529u, Assert.Single(saved.ToolEvents.Mappings).Ceid);
            Assert.NotSame(original.ToolEvents, saved.ToolEvents);   // a copy, not the live object
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
