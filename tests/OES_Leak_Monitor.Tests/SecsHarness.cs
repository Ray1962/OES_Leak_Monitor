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
    private readonly List<Item> _eventBodies = new();
    private readonly List<string> _alids = new();
    private readonly List<TraceSample> _traces = new();

    private GemHost _host = null!;
    private SecsSettings _settings = null!;
    private int _port;

    public SecsBridge Bridge { get; private set; } = null!;

    /// <summary>One S6F1: <c>L{4} TRID, SMPLN, STIME, L{values}</c>, values in SVID order.</summary>
    public sealed record TraceSample(uint Trid, uint SampleNumber, string Stamp, IReadOnlyList<Item> Values);

    /// <summary>Event reports received so far, in arrival order.</summary>
    public IReadOnlyList<uint> Ceids() { lock (_ceids) { return _ceids.ToArray(); } }

    public int Count { get { lock (_ceids) { return _ceids.Count; } } }

    /// <summary>The whole S6F11 body of each event report, in arrival order:
    /// <c>L{3} DATAID, CEID, L{ L{2} RPTID, L{values} }</c>.</summary>
    public IReadOnlyList<Item> EventBodies() { lock (_ceids) { return _eventBodies.ToArray(); } }

    /// <summary>Alarm ids received so far, as sent — ASCII, per specification §5.3.</summary>
    public IReadOnlyList<string> Alids() { lock (_alids) { return _alids.ToArray(); } }

    public int Alarms { get { lock (_alids) { return _alids.Count; } } }

    /// <summary>Trace samples received so far, in arrival order.</summary>
    public IReadOnlyList<TraceSample> TraceSamples() { lock (_traces) { return _traces.ToArray(); } }

    public int Samples { get { lock (_traces) { return _traces.Count; } } }

    /// <param name="prepare">Called with the config folder before the interface first starts —
    /// for a test that needs a site profile already there.</param>
    public static async Task<SecsHarness> StartAsync(Action<SecsSettings>? configure = null,
        SystemLogger? log = null, Action<string>? prepare = null)
    {
        var h = new SecsHarness();
        var port = SecsTestPort.Free();
        Directory.CreateDirectory(h._folder);
        prepare?.Invoke(h._folder);

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
        h._settings = settings;

        h.Bridge = new SecsBridge(log, h._folder, h._folder,
            () => new SecsBridge.AcquisitionInfo(120f, 4u));
        h.Bridge.Configure(settings);

        h._port = port;
        await h.ConnectHostAsync();
        return h;
    }

    /// <summary>
    /// A fresh <see cref="GemHost"/> on the same port, wired to the same collections, waited
    /// until it is Communicating. Used at start-up and by <see cref="ReconnectHostAsync"/>.
    /// </summary>
    private async Task ConnectHostAsync()
    {
        _host = new GemHost(
            new SecsGemOptions { IsActive = true, IpAddress = "127.0.0.1", Port = _port },
            new GemOptions
            {
                ModelName = "TEST-HOST",
                SoftwareRevision = "1.0",
                EstablishRetryIntervalMs = 1000,
            });
        // S6F11 carries L{3} DATAID, CEID, reports — the CEID is U4 (only ALID is ASCII).
        _host.EventReportReceived += m =>
        {
            lock (_ceids)
            {
                _ceids.Add(m.Raw.SecsItem![1].FirstValue<uint>());
                _eventBodies.Add(m.Raw.SecsItem!);
            }
        };
        _host.AlarmReportReceived += m =>
        {
            lock (_alids) { _alids.Add(m.Raw.SecsItem![1].GetString()); }
        };
        _host.TraceReportReceived += m =>
        {
            var body = m.Raw.SecsItem!;
            var values = new List<Item>();
            for (var i = 0; i < body[3].Count; i++)
            {
                values.Add(body[3][i]);
            }
            lock (_traces)
            {
                _traces.Add(new TraceSample(
                    body[0].FirstValue<uint>(), body[1].FirstValue<uint>(), body[2].GetString(), values));
            }
        };
        _host.Start();
        _host.Enable();

        Assert.True(await SecsTestPort.WaitAsync(
            () => _host.CommunicationState == CommunicationState.Communicating, 20),
            "the host and the equipment never reached Communicating");
    }

    /// <summary>
    /// Drops the TCP link from the host side and connects a new host — a host that crashed or
    /// was restarted. The equipment instance is untouched, unlike <see cref="RestartAsync"/>.
    /// </summary>
    public async Task ReconnectHostAsync()
    {
        await _host.DisposeAsync();
        Assert.True(await SecsTestPort.WaitAsync(
            () => Bridge.State != SecsRunState.Communicating, 20),
            "the equipment never noticed the host had gone");
        await ConnectHostAsync();
        Assert.True(await SecsTestPort.WaitAsync(
            () => Bridge.State == SecsRunState.Communicating, 20),
            "the equipment never got back to Communicating with the new host");
    }

    /// <summary>
    /// Sends a primary message as the host and returns the reply — for the cases
    /// <see cref="GemHost"/> only logs (S1F4) or reduces to a bool (S2F24, S2F34…), where the
    /// test needs the actual item. <c>GemHost</c> keeps its transport private; the
    /// <see cref="SecsGemHost"/> inside it is public and sends exactly what it is given.
    /// </summary>
    public Task<SecsMessage> SendRawAsync(SecsMessage message)
    {
        var field = typeof(GemHost).GetField("_host",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GemHost no longer has a _host field");
        var transport = (SecsGemHost)field.GetValue(_host)!;
        return transport.SendAsync(message);
    }

    /// <summary>What Restart interface does — re-reads the profile — then waits for the host to
    /// be talking to the new instance.</summary>
    public async Task RestartAsync()
    {
        Bridge.Configure(_settings);
        Assert.True(await SecsTestPort.WaitAsync(
            () => _host.CommunicationState == CommunicationState.Communicating
                  && Bridge.State == SecsRunState.Communicating, 20),
            $"never got back to Communicating after a restart (\"{Bridge.LastError}\")");
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
