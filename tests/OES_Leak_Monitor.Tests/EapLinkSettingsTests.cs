using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// What the EAP link asks the EAP for when S4 or the tool-link CEID is not provided (#64):
/// the S1F3 list, the S2F37 list, the id checks Save and the link share, and the sentence that
/// says what the OES then cannot see.
/// </summary>
public sealed class EapLinkSettingsTests
{
    private static EapLinkSettings All() => new()
    {
        WindowStateSvid = 6001, WindowCeidSvid = 6002, WindowStartSvid = 6003, ToolLinkSvid = 6004,
        LastCeidSvid = 6005, LastTimeSvid = 6006, SequenceSvid = 6007, ForwardCeid = 6100, LinkCeid = 6101,
    };

    [Fact]
    public void WithEverythingProvided_ItAsksForAllOfIt()
    {
        var s = All();
        Assert.Null(s.IdProblem());
        Assert.Null(s.ToolLinkGap());
        Assert.Equal(new uint[] { 6001, 6002, 6003, 6004, 6005, 6006, 6007 }, s.QueriedSvids);
        Assert.Equal(new uint[] { 6100, 6101 }, s.EnabledCeids);
    }

    [Fact]
    public void WithoutS4_TheS1F3LeavesItOut()
    {
        var s = All();
        s.ToolLinkSvid = 0;
        Assert.Null(s.IdProblem());
        Assert.Equal(new uint[] { 6001, 6002, 6003, 6005, 6006, 6007 }, s.QueriedSvids);
        Assert.Contains("no S4", s.ToolLinkGap());
    }

    [Fact]
    public void WithoutTheToolLinkCeid_S2F37EnablesTheForwardCeidAlone()
    {
        var s = All();
        s.LinkCeid = 0;
        Assert.Null(s.IdProblem());
        Assert.Equal(new uint[] { 6100 }, s.EnabledCeids);
        Assert.Contains("no tool-link CEID", s.ToolLinkGap());
    }

    [Fact]
    public void WithNeither_TheGapSaysTheLinkIsSeenOnlyThroughS1()
    {
        var s = All();
        s.ToolLinkSvid = 0;
        s.LinkCeid = 0;
        Assert.Null(s.IdProblem());
        Assert.Contains("neither S4 nor the tool-link CEID", s.ToolLinkGap());
        Assert.Contains("S1 = 2", s.ToolLinkGap());
    }

    /// <summary>The seven required ids are still required, and the message names which.</summary>
    [Fact]
    public void ARequiredIdLeftUnset_IsNamed()
    {
        var s = All();
        s.WindowCeidSvid = 0;
        s.ForwardCeid = 0;
        Assert.Equal("the EAP ids are not all set (S2, forwarding CEID)", s.IdProblem());
    }

    /// <summary>An optional S4 of 0 is not a duplicate; a real one still is.</summary>
    [Fact]
    public void DuplicatesAreJudgedOnTheIdsThatAreSet()
    {
        var s = All();
        s.ToolLinkSvid = 6001;
        Assert.Equal("two of S1–S7 share an SVID", s.IdProblem());
        s.ToolLinkSvid = 0;
        Assert.Null(s.IdProblem());
    }

    [Fact]
    public void ReadingWithoutS4_TakesSixValuesAndHasNoToolLink()
    {
        var six = new Secs4Net.Item[]
        {
            Secs4Net.Item.U1(1), Secs4Net.Item.U4(3100439529), Secs4Net.Item.A("2026102714014000"),
            Secs4Net.Item.U4(3100439529), Secs4Net.Item.A("2026102714014000"), Secs4Net.Item.U4(9),
        };
        var r = EapSecsLink.ParseReading(six, hasToolLink: false);
        Assert.NotNull(r);
        Assert.Null(r!.ToolLink);
        Assert.Equal(1, r.WindowState);
        Assert.Equal(3100439529u, r.LastCeid);
        Assert.Equal(9u, r.Sequence);
        // The same six values read as if S4 were there are not a reading at all.
        Assert.Null(EapSecsLink.ParseReading(six, hasToolLink: true));
    }
}
