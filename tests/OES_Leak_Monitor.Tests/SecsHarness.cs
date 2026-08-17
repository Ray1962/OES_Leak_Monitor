using System.IO;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// A <see cref="SecsBridge"/> and a <see cref="GemHost"/> connected over loopback, collecting
/// everything the host receives: event reports, alarms and trace samples.
///
/// <para>Built per test rather than once per class, because every setting these tests vary is
/// read when the interface starts — reconfiguring a running one would make the host reconnect
/// mid-assertion.</para>
/// </summary>
internal sealed class SecsHarness : IAsyncDisposable
{
    /// <summary>Ch_2 throughout, so the expected ids can be written out in full.</summary>
    public const int Chamber = 2;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oes-secs-" + Guid.NewGuid().ToString("N"));
    private readonly List<uint> _ceids = new();
    private readonly List<string> _alids = new();
    private readonly List<TraceSample> _traces = new();

    private GemHost _host = null!;

    public SecsBridge Bridge { get; private set; } = null!;

    /// <summary>One S6F1: <c>L{4} TRID, SMPLN, STIME, L{values}</c>, values in SVID order.</summary>
    public sealed record TraceSample(uint Trid, uint SampleNumber, string Stamp, IReadOnlyList<Item> Values);

    /// <summary>Event reports received so far, in arrival order.</summary>
    public IReadOnlyList<uint> Ceids() { lock (_ceids) { return _ceids.ToArray(); } }

    public int Count { get { lock (_ceids) { return _ceids.Count; } } }

    /// <summary>Alarm ids received so far, as sent — ASCII, per specification §5.3.</summary>
    public IReadOnlyList<string> Alids() { lock (_alids) { return _alids.ToArray(); } }

    public int Alarms { get { lock (_alids) { return _alids.Count; } } }

    /// <summary>Trace samples received so far, in arrival order.</summary>
    public IReadOnlyList<TraceSample> TraceSamples() { lock (_traces) { return _traces.ToArray(); } }

    public int Samples { get { lock (_traces) { return _traces.Count; } } }

    public static async Task<SecsHarness> StartAsync(Action<SecsSettings>? configure = null)
    {
        var h = new SecsHarness();
        var port = SecsTestPort.Free();
        Directory.CreateDirectory(h._folder);

        var settings = new SecsSettings
        {
            Enabled = true,
            ChamberCode = Chamber,
            IpAddress = "127.0.0.1",
            Port = port,
            // A test has only synthetic frames to offer, so reporting them is the default here;
            // the tests that are about the gate itself turn it back off.
            ReportInTestMode = true,
        };
        configure?.Invoke(settings);

        h.Bridge = new SecsBridge(null, h._folder, h._folder,
            () => new SecsBridge.AcquisitionInfo(120f, 4u));
        h.Bridge.Configure(settings);

        h._host = new GemHost(
            new SecsGemOptions { IsActive = true, IpAddress = "127.0.0.1", Port = port },
            new GemOptions
            {
                ModelName = "TEST-HOST",
                SoftwareRevision = "1.0",
                EstablishRetryIntervalMs = 1000,
            });
        // S6F11 carries L{3} DATAID, CEID, reports — the CEID is U4 (only ALID is ASCII).
        h._host.EventReportReceived += m =>
        {
            lock (h._ceids) { h._ceids.Add(m.SecsItem![1].FirstValue<uint>()); }
        };
        h._host.AlarmReceived += m =>
        {
            lock (h._alids) { h._alids.Add(m.SecsItem![1].GetString()); }
        };
        h._host.TraceDataReceived += m =>
        {
            var body = m.SecsItem!;
            var values = new List<Item>();
            for (var i = 0; i < body[3].Count; i++)
            {
                values.Add(body[3][i]);
            }
            lock (h._traces)
            {
                h._traces.Add(new TraceSample(
                    body[0].FirstValue<uint>(), body[1].FirstValue<uint>(), body[2].GetString(), values));
            }
        };
        h._host.Start();
        h._host.Enable();

        Assert.True(await SecsTestPort.WaitAsync(
            () => h._host.CommunicationState == CommunicationState.Communicating, 20),
            "the host and the equipment never reached Communicating");
        return h;
    }

    /// <summary>The host end, for the requests a test drives itself (S2F23, S5F5, S1F3).</summary>
    public GemHost Host => _host;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Bridge.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }
}
