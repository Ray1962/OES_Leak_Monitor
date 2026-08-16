using System.Text.Json;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Chamber stamping: turning the shipped <c>cc = 00</c> profile into one for the chamber this
/// tool actually watches. Every SVID, ALID and CEID the host sees comes out of here, so a
/// silent mistake is a tool reporting under another sensor's identity.
/// </summary>
public class SecsChamberCodingTests
{
    // Worked examples straight out of docs/Satellite_SECS_Specification_v2.md §1.4, §5, §6.
    [Theory]
    [InlineData(2, 1, 1022700001u)]     // §1.4 ss=27: Ch_2 leak rate
    [InlineData(2, 26, 1022700026u)]
    [InlineData(11, 26, 1112700026u)]   // Buffer / Buffer 1
    public void Svid_follows_the_specification(int chamber, int vid, uint expected) =>
        Assert.Equal(expected, SecsChamberCoding.Svid(chamber, vid));

    [Theory]
    [InlineData(2, 2, 10227002u)]       // §5: Ch_2 leak alarm
    [InlineData(2, 508, 10227508u)]     // §6: acquisition started
    [InlineData(11, 502, 11127502u)]
    public void EventId_follows_the_specification(int chamber, int nnn, uint expected) =>
        Assert.Equal(expected, SecsChamberCoding.EventId(chamber, nnn));

    [Fact]
    public void Chamber_codes_come_from_the_specification_table()
    {
        Assert.Equal("Ch_2", SecsChamberCoding.ChamberName(2));
        Assert.Equal("Buffer / Buffer 1", SecsChamberCoding.ChamberName(11));

        // 00 is in the specification's table but means "no slit-valve signal collected", not a
        // chamber. It is what the profile template ships with, so accepting it would let an
        // unstamped profile pass for a stamped one.
        Assert.False(SecsChamberCoding.IsValidChamber(0));
        Assert.False(SecsChamberCoding.IsValidChamber(16));   // gap in the table
        Assert.False(SecsChamberCoding.IsValidChamber(35));
        Assert.All(SecsChamberCoding.ValidChamberCodes, c => Assert.True(SecsChamberCoding.IsValidChamber(c)));
    }

    // ---- stamping the profile ---------------------------------------------

    [Fact]
    public void Stamps_every_id_in_the_shipped_template()
    {
        using var doc = JsonDocument.Parse(SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 2));
        var svs = doc.RootElement.GetProperty("statusVariables");
        var alarms = doc.RootElement.GetProperty("alarms");

        Assert.Equal(26, svs.GetArrayLength());
        Assert.Equal(1022700001, svs[0].GetProperty("svid").GetInt64());
        Assert.Equal(1022700026, svs[25].GetProperty("svid").GetInt64());
        Assert.All(svs.EnumerateArray(), sv =>
        {
            var text = sv.GetProperty("svid").GetInt64().ToString();
            Assert.Equal(10, text.Length);
            Assert.Equal("10227", text[..5]);      // 1 + cc=02 + ss=27
            Assert.Equal("00", text.Substring(5, 2));  // aa fixed at 00 for ss=27
        });

        Assert.Equal(
            new long[] { 10227001, 10227002, 10227012, 10227013, 10227014 },
            alarms.EnumerateArray().Select(a => a.GetProperty("alid").GetInt64()));
    }

    [Fact]
    public void Bind_names_are_left_alone()
    {
        using var doc = JsonDocument.Parse(SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 5));
        Assert.Equal("oes.leakRate",
            doc.RootElement.GetProperty("statusVariables")[0].GetProperty("bind").GetString());
    }

    [Fact]
    public void An_already_stamped_profile_can_be_re_stamped()
    {
        // The tool is moved to another chamber, or the code was simply typed wrong the first
        // time: stamping is applied to the template each start-up, so it has to be total.
        var once = SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 2);
        var twice = SecsChamberCoding.ApplyChamber(once, 2);
        var moved = SecsChamberCoding.ApplyChamber(once, 11);

        Assert.Equal(1022700001, FirstSvid(twice));
        Assert.Equal(1112700001, FirstSvid(moved));
    }

    // ---- refusals ---------------------------------------------------------

    [Fact]
    public void A_chamber_the_specification_does_not_name_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 0));
        Assert.Contains("chamber code 00", ex.Message);
    }

    [Fact]
    public void An_id_belonging_to_another_sensor_type_is_refused()
    {
        // A hand-edited profile with one digit wrong. Left alone it would reach the host as a
        // plausible id belonging to a different sensor — which is worse than not connecting.
        var wrong = SecsProfileTemplate.Json.Replace("1002700001", "1002600001");
        var ex = Assert.Throws<InvalidOperationException>(() => SecsChamberCoding.ApplyChamber(wrong, 2));
        Assert.Contains("sensor code 26", ex.Message);
    }

    [Fact]
    public void A_non_zero_slit_valve_code_is_refused()
    {
        // §1.4 fixes aa at 00 for ss=27: the OES watches the plasma directly.
        var wrong = SecsProfileTemplate.Json.Replace("1002700001", "1002701001");
        var ex = Assert.Throws<InvalidOperationException>(() => SecsChamberCoding.ApplyChamber(wrong, 2));
        Assert.Contains("slit-valve code 01", ex.Message);
    }

    [Fact]
    public void An_id_that_is_not_ours_is_left_untouched()
    {
        // A site may add a constant of its own — a machine id, say — with an id outside the
        // Satellite scheme. Rewriting it would corrupt it.
        var extra = SecsProfileTemplate.Json.Replace(
            "\"statusVariables\": [",
            "\"statusVariables\": [\n    { \"svid\": 900, \"name\": \"MachineId\", \"format\": \"A\", \"value\": \"OES-01\" },");
        using var doc = JsonDocument.Parse(SecsChamberCoding.ApplyChamber(extra, 2));
        Assert.Equal(900, doc.RootElement.GetProperty("statusVariables")[0].GetProperty("svid").GetInt64());
    }

    private static long FirstSvid(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("statusVariables")[0].GetProperty("svid").GetInt64();
    }
}
