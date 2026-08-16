using System.IO;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// <see cref="SecsBridge"/> against a real host over loopback: what a fab MES actually receives.
/// Real sockets on purpose — the things worth catching here (id encoding, item formats, the
/// ASCII ALID, set/clear ordering) only exist on the wire.
/// </summary>
[Collection("network")]
public class SecsWireTests : IAsyncLifetime
{
    private const int Chamber = 2;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oes-secs-wire-" + Guid.NewGuid().ToString("N"));

    private SecsBridge _bridge = null!;
    private GemHost _host = null!;

    public async Task InitializeAsync()
    {
        var port = SecsTestPort.Free();
        Directory.CreateDirectory(_folder);

        _bridge = new SecsBridge(null, _folder, _folder,
            () => new SecsBridge.AcquisitionInfo(120f, 4u));
        _bridge.Configure(new SecsSettings
        {
            Enabled = true,
            ChamberCode = Chamber,
            IpAddress = "127.0.0.1",
            Port = port,
            // Replayed and synthetic frames are the only ones a test has, and the point here is
            // the wire format rather than the test-mode policy (which SecsReportingPolicyTests covers).
            ReportInTestMode = true,
        });

        _host = new GemHost(
            new SecsGemOptions { IsActive = true, IpAddress = "127.0.0.1", Port = port },
            new GemOptions { ModelName = "TEST-HOST", SoftwareRevision = "1.0", EstablishRetryIntervalMs = 1000 });
        _host.Start();
        _host.Enable();

        Assert.True(await SecsTestPort.WaitAsync(
            () => _host.CommunicationState == CommunicationState.Communicating, 20),
            "the host and the equipment never reached Communicating");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _bridge.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Answers_a_status_request_with_every_status_variable()
    {
        _bridge.OnSample(SnapshotBuilder.Leaking());

        string log = "";
        void Capture(string line) { if (line.Contains("S1F3")) log = line; }
        _bridge.Traffic += (_, line) => Capture(line);

        await _host.RequestStatusAsync(Array.Empty<uint>());

        Assert.True(await SecsTestPort.WaitAsync(() => log.Contains("(26 SV)"), 10),
            "equipment did not answer S1F3 with all 26 status variables; last line: " + log);
    }

    [Fact]
    public async Task Lists_its_alarms_with_the_stamped_ids_in_ASCII()
    {
        var alarms = await _host.ListAlarmsAsync();

        Assert.Equal(
            new uint[] { 10227001, 10227002, 10227012, 10227013, 10227014 },
            alarms.Select(a => a.Alid));
        // SEMI E5 categories: 6 equipment status warning, 4 parameter control error,
        // 5 irrecoverable error, 8 data integrity.
        Assert.Equal(new byte[] { 6, 4, 5, 5, 8 }, alarms.Select(a => a.Category));
    }

    [Fact]
    public async Task A_leak_alarm_goes_out_as_S5F1_with_an_ASCII_ALID_and_live_text()
    {
        _bridge.OnSample(SnapshotBuilder.Leaking());

        SecsMessage? received = null;
        _host.AlarmReceived += m => received = m;

        _bridge.OnLeakLevelChanged(LeakAlarmLevel.Alarm);

        Assert.True(await SecsTestPort.WaitAsync(() => received is not null, 10), "no S5F1 arrived");
        var body = received!.SecsItem!;

        Assert.Equal(0x84, body[0].FirstValue<byte>());              // set + category 4
        // The Satellite specification writes ALID as ASCII digits throughout (§4, §5.3).
        Assert.Equal(SecsFormat.ASCII, body[1].Format);
        Assert.Equal("10227002", body[1].GetString());
        // §5.3 asks for text a host's alarm log can be read on its own.
        var text = body[2].GetString();
        Assert.Contains("CH2 OES LEAK ALARM", text);
        Assert.Contains("1 alarm / 1 warning of 3 ratios", text);
        Assert.Contains("mbar-L/s", text);
    }

    [Fact]
    public async Task Rising_to_alarm_and_falling_back_produces_a_set_then_a_clear()
    {
        _bridge.OnSample(SnapshotBuilder.Leaking());

        var seen = new List<(string Alid, bool Set)>();
        _host.AlarmReceived += m =>
        {
            lock (seen)
            {
                seen.Add((m.SecsItem![1].GetString(), (m.SecsItem[0].FirstValue<byte>() & 0x80) != 0));
            }
        };

        // The sequence a real leak produces: warning, confirmed alarm, then an acknowledge.
        _bridge.OnLeakLevelChanged(LeakAlarmLevel.Warning);
        _bridge.OnLeakLevelChanged(LeakAlarmLevel.Alarm);
        _bridge.OnLeakLevelChanged(LeakAlarmLevel.Normal);

        Assert.True(await SecsTestPort.WaitAsync(() => Count(seen) >= 4, 10),
            "expected four alarm messages, got " + Count(seen));

        lock (seen)
        {
            Assert.Equal(new[]
            {
                ("10227001", true),    // warning set
                ("10227001", false),   // warning clear as it escalates
                ("10227002", true),    // alarm set
                ("10227002", false),   // alarm clear on the way down
            }, seen);
        }

        static int Count(List<(string, bool)> list) { lock (list) { return list.Count; } }
    }

    [Fact]
    public async Task An_equipment_fault_is_reported_and_de_bounced()
    {
        var seen = new List<(string Alid, bool Set)>();
        _host.AlarmReceived += m =>
        {
            lock (seen)
            {
                seen.Add((m.SecsItem![1].GetString(), (m.SecsItem[0].FirstValue<byte>() & 0x80) != 0));
            }
        };

        _bridge.ReportFault(SecsFault.ConnectionLost, set: true, detail: "cable pulled");
        _bridge.ReportFault(SecsFault.ConnectionLost, set: true, detail: "still gone");   // ignored
        _bridge.ReportFault(SecsFault.ConnectionLost, set: false);
        _bridge.ReportFault(SecsFault.ConnectionLost, set: false);                        // ignored

        Assert.True(await SecsTestPort.WaitAsync(() => Count(seen) >= 2, 10));
        await Task.Delay(300);   // let any extra message that should not exist arrive

        lock (seen)
        {
            Assert.Equal(new[] { ("10227012", true), ("10227012", false) }, seen);
        }

        static int Count(List<(string, bool)> list) { lock (list) { return list.Count; } }
    }
}
