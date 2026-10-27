using System;
using System.Linq;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Xunit;
using static Secs4Net.Item;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Matching the EAP's namelists against the spec's agreed names (#66). Exact, trimmed and
/// case-insensitive — an id read by the wrong variable is a window read wrong, so anything short
/// of the agreed name is left for the engineer, with the reason.
/// </summary>
public sealed class EapIdDiscoveryTests
{
    private static StatusVariableName Sv(uint id, string name) => new(id, name, "");
    private static CollectionEventName Ce(uint id, string name) => new(id, name, Array.Empty<uint>());

    private static readonly StatusVariableName[] AllSvs =
    {
        Sv(6001, "PM3_WINDOW_STATE"), Sv(6002, "PM3_WINDOW_CEID"), Sv(6003, "PM3_WINDOW_START"),
        Sv(6004, "PM3_TOOL_LINK"), Sv(6005, "PM3_LAST_CEID"), Sv(6006, "PM3_LAST_TIME"), Sv(6007, "PM3_EVENT_SEQ"),
        Sv(9999, "SOMETHING_ELSE"),
    };

    private static readonly CollectionEventName[] AllCes = { Ce(6100, "PM3_EVENT_FORWARD"), Ce(6101, "PM3_TOOL_LINK_CHANGED") };

    [Fact]
    public void EveryAgreedName_IsFound()
    {
        var m = EapIdDiscovery.Match(AllSvs, AllCes);
        Assert.All(m, x => Assert.Equal(EapIdMatchState.Found, x.State));
        Assert.Equal(new uint[] { 6001, 6002, 6003, 6004, 6005, 6006, 6007, 6100, 6101 }, m.Select(x => x.Id));
    }

    [Fact]
    public void NamesMatch_TrimmedAndCaseInsensitive_ButNotLoosely()
    {
        var svs = AllSvs.Select(v => v.Svid == 6001 ? Sv(6001, "  pm3_window_state ") : v)
                        .Select(v => v.Svid == 6002 ? Sv(6002, "PM3_WINDOW_CEID_2") : v).ToArray();
        var m = EapIdDiscovery.Match(svs, AllCes);
        Assert.Equal(EapIdMatchState.Found, m.Single(x => x.Label == "S1").State);
        Assert.Equal(EapIdMatchState.NotFound, m.Single(x => x.Label == "S2").State);
        Assert.Equal("PM3_WINDOW_CEID not found", m.Single(x => x.Label == "S2").Note);
    }

    [Fact]
    public void ANameOnTwoIds_IsAmbiguous_AndNotOffered()
    {
        var svs = AllSvs.Append(Sv(7001, "PM3_WINDOW_STATE")).ToArray();
        var s1 = EapIdDiscovery.Match(svs, AllCes).Single(x => x.Label == "S1");
        Assert.Equal(EapIdMatchState.Ambiguous, s1.State);
        Assert.Equal("PM3_WINDOW_STATE matches SVID 6001 and 7001", s1.Note);
        Assert.False(s1.Offered);
    }

    /// <summary>The two optional ids missing is #64's "not provided", not an error — and Use
    /// writes 0 for them.</summary>
    [Fact]
    public void OptionalIdsNotFound_AreNotProvided()
    {
        var svs = AllSvs.Where(v => v.Svid != 6004).ToArray();
        var m = EapIdDiscovery.Match(svs, new[] { Ce(6100, "PM3_EVENT_FORWARD") });
        Assert.Equal(EapIdMatchState.NotProvided, m.Single(x => x.Label == "S4").State);
        Assert.Equal(EapIdMatchState.NotProvided, m.Single(x => x.Label == "tool-link CEID").State);

        var settings = new EapLinkSettings { ToolLinkSvid = 1234, LinkCeid = 5678 };
        new EapIdDiscovery(DateTime.Now, m, svs, Array.Empty<CollectionEventName>(), "").ApplyTo(settings);
        Assert.Equal(0u, settings.ToolLinkSvid);
        Assert.Equal(0u, settings.LinkCeid);
        Assert.Null(settings.IdProblem());
    }

    /// <summary>A namelist the EAP did not answer is said as such, not as "not found".</summary>
    [Fact]
    public void AnUnansweredNamelist_IsNamedInTheNote()
    {
        var m = EapIdDiscovery.Match(AllSvs, null);
        Assert.Equal("S1F23 not answered", m.Single(x => x.Label == "forward CEID").Note);
        Assert.Equal(EapIdMatchState.Found, m.Single(x => x.Label == "S1").State);
    }

    [Fact]
    public void TheFormatCheck_FlagsTheWrongKind_AndOnlyThat()
    {
        var m = EapIdDiscovery.Match(AllSvs, AllCes);
        Assert.Equal(new uint[] { 6001, 6002, 6003, 6004, 6005, 6006, 6007 }, EapIdDiscovery.SvidsToCheck(m));
        // S1 U1, S2 U4, S3 A, S4 U1, S5 U4, S6 a number where text belongs, S7 U4.
        var values = new[] { U1(1), U4(3100439529), A(""), U1(1), U4(0), U4(0), U4(5) };
        var checkedM = EapIdDiscovery.CheckFormats(m, values);
        var s6 = checkedM.Single(x => x.Label == "S6");
        Assert.Equal(EapIdMatchState.WrongType, s6.State);
        Assert.Contains("text (A) expected", s6.Note);
        Assert.Single(checkedM, x => x.State == EapIdMatchState.WrongType);
        // A failed read checks nothing.
        Assert.Same(m, EapIdDiscovery.CheckFormats(m, null));
    }

    /// <summary>Use takes found and wrong-type ids, leaves a required id that was not found or
    /// was ambiguous as it was.</summary>
    [Fact]
    public void ApplyTo_TakesOnlyWhatWasOffered()
    {
        var svs = AllSvs.Where(v => v.Svid != 6002).Append(Sv(7003, "PM3_WINDOW_START")).ToArray();
        var m = EapIdDiscovery.Match(svs, AllCes);
        var d = new EapIdDiscovery(DateTime.Now, m, svs, AllCes, "");
        var settings = new EapLinkSettings { WindowCeidSvid = 42, WindowStartSvid = 43 };

        d.ApplyTo(settings);

        Assert.Equal(6001u, settings.WindowStateSvid);
        Assert.Equal(42u, settings.WindowCeidSvid);   // not found: untouched
        Assert.Equal(43u, settings.WindowStartSvid);  // ambiguous: untouched
        Assert.Equal(6100u, settings.ForwardCeid);
        Assert.True(d.HasOffer);
        Assert.Contains("7 of 9 agreed names found", d.Summary);
    }
}
