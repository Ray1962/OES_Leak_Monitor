using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;
using static Secs4Net.Item;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// S2F41 <c>TOOL_EVENT</c> over a real loopback link: what the EAP is answered with, and that
/// the event reaches the leak monitor. What the window then does to the judgement is
/// <c>ToolEventWindowTests</c>' business; this class only checks the command gets through.
/// </summary>
[Collection("network")]
public class ToolEventWireTests
{
    private const uint SvidToolEventState = 1022700030;
    private const uint SvidToolWindowClass = 1022700031;
    private const uint SvidLastToolCeid = 1022700032;

    private const uint ProcessStart = 3100439529;

    /// <summary>A host log line per S2F41, as <see cref="GemHost"/> reports the HCACK it got.</summary>
    private static async Task<string> SendAsync(SecsHarness h, params (string Name, string Value)[] parameters)
    {
        var lines = new List<string>();
        void Capture(string line) { if (line.Contains("S2F41")) lock (lines) lines.Add(line); }
        h.Host.Log += Capture;
        try
        {
            await h.Host.SendRemoteCommandAsync("TOOL_EVENT", parameters);
            Assert.True(await SecsTestPort.WaitAsync(() => { lock (lines) return lines.Count > 0; }, 10),
                "the host logged no S2F41 reply");
            lock (lines) return lines[^1];
        }
        finally
        {
            h.Host.Log -= Capture;
        }
    }

    [Fact]
    public async Task With_nothing_to_take_events_the_answer_is_cannot_perform_now()
    {
        await using var h = await SecsHarness.StartAsync();

        var reply = await SendAsync(h, ("CEID", ProcessStart.ToString()));

        Assert.Contains("HCACK=2", reply);
    }

    [Fact]
    public async Task Tool_events_switched_off_are_answered_cannot_perform_now()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.ToolEventSink = (_, _) => ToolEventAck.Disabled;

        var reply = await SendAsync(h, ("CEID", ProcessStart.ToString()));

        Assert.Contains("HCACK=2", reply);
    }

    [Fact]
    public async Task An_accepted_event_reaches_the_sink_and_is_answered_zero()
    {
        await using var h = await SecsHarness.StartAsync();
        var received = new List<uint>();
        h.Bridge.ToolEventSink = (ceid, _) => { lock (received) received.Add(ceid); return ToolEventAck.Accepted; };

        var reply = await SendAsync(h, ("CEID", ProcessStart.ToString()), ("TIME", "2026102214030000"));

        Assert.Contains("HCACK=0", reply);
        lock (received) Assert.Equal(new[] { ProcessStart }, received);
    }

    /// <summary>
    /// A CEID the table does not know is acknowledged, not refused, so the EAP can forward the
    /// tool's events unfiltered — and the traffic log says it was ignored, so nobody has to
    /// wonder whether an event they expected to matter arrived.
    /// </summary>
    [Fact]
    public async Task An_unmapped_CEID_is_answered_zero_and_logged_as_ignored()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.ToolEventSink = (_, _) => ToolEventAck.Ignored;
        var traffic = new List<string>();
        h.Bridge.Traffic += (_, line) => { lock (traffic) traffic.Add(line); };

        var reply = await SendAsync(h, ("CEID", "2697019492"));

        Assert.Contains("HCACK=0", reply);
        Assert.True(await SecsTestPort.WaitAsync(
            () => { lock (traffic) return traffic.Any(l => l.Contains("TOOL_EVENT") && l.Contains("ignored")); }, 5),
            "the traffic log did not say the event was ignored");
    }

    [Theory]
    [InlineData(null)]          // no CEID at all
    [InlineData("PM3-START")]   // not a number
    [InlineData("-1")]
    public async Task A_missing_or_unreadable_CEID_is_a_parameter_error(string? ceid)
    {
        await using var h = await SecsHarness.StartAsync();
        bool called = false;
        h.Bridge.ToolEventSink = (_, _) => { called = true; return ToolEventAck.Accepted; };

        var reply = ceid is null
            ? await SendAsync(h, ("TIME", "2026102214030000"))
            : await SendAsync(h, ("CEID", ceid));

        Assert.Contains("HCACK=3", reply);
        Assert.False(called);
    }

    /// <summary>VIDs 030–032 carry what the snapshot holds, so the EAP can read back which
    /// window the leak monitor believes it is in and the last event it received.</summary>
    [Fact]
    public async Task The_tool_event_status_variables_report_the_snapshot()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(new LeakMonitorSnapshot
        {
            Timestamp = DateTime.Now,
            ToolEventState = ToolEventContext.InWindow,
            ToolWindowClass = "A",
            LastToolCeid = ProcessStart,
        });

        Assert.True(
            await h.Host.StartTraceAsync(trid: 30, TimeSpan.FromMilliseconds(200), totalSamples: 2, groupSize: 1,
                new[] { SvidToolEventState, SvidToolWindowClass, SvidLastToolCeid }),
            "the equipment rejected S2F23 (TIAACK != 0)");
        Assert.True(await SecsTestPort.WaitAsync(() => h.Samples >= 1, 10), "no trace sample arrived");

        var values = h.TraceSamples()[0].Values;
        Assert.Equal(2u, values[0].FirstValue<uint>());          // InWindow
        Assert.Equal("A", values[1].GetString());
        Assert.Equal(ProcessStart, values[2].FirstValue<uint>());
    }

    // ---------------------------------------------------------------- CEID parsing

    [Fact]
    public void A_CEID_is_read_from_ASCII_digits_or_an_unsigned_item()
    {
        Assert.Equal(ProcessStart, SecsBridge.ParseCeid(A(" 3100439529 ")));
        Assert.Equal(ProcessStart, SecsBridge.ParseCeid(U4(ProcessStart)));
        Assert.Equal(ProcessStart, SecsBridge.ParseCeid(U8((ulong)ProcessStart)));
        Assert.Equal(7u, SecsBridge.ParseCeid(U2(7)));
        Assert.Null(SecsBridge.ParseCeid(A("abc")));
        Assert.Null(SecsBridge.ParseCeid(U8(5_000_000_000UL)));      // does not fit a CEID
        Assert.Null(SecsBridge.ParseCeid(L()));
    }
}
