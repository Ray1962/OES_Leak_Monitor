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
}
