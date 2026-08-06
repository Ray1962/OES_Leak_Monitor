namespace OES_Leak_Monitor;

/// <summary>
/// Housekeeping policy for the logger's data tree. The program never deletes measurement
/// data: an expired day folder is <b>compressed into a sibling <c>DD.zip</c></b>, which
/// reclaims most of the space (CSV compresses roughly 10–20×) while keeping every row
/// recoverable with any unzip tool. Deleting is left to whoever owns the retention rules.
/// <para/>
/// Two rules, in this order:
/// <list type="number">
/// <item><b>Age</b> — a day folder older than <see cref="ArchiveAfterDays"/> is archived.
/// This is the primary rule, because "we keep N days uncompressed" is what an audit asks
/// about.</item>
/// <item><b>Total size</b> — if the tree still exceeds <see cref="MaxTotalSizeGB"/>,
/// archiving continues into newer folders, oldest first, until it fits. A safety net for
/// the case the age rule alone can't hold (a day of full-spectrum logging is ~10 GB).</item>
/// </list>
/// Neither rule ever touches the newest <see cref="MinKeepDays"/> days or a folder holding
/// a file the logger currently has open.
/// </summary>
public sealed class DataRetentionSettings
{
    /// <summary>
    /// Master switch for the archiver, <b>off by default</b>. Free-space and size warnings
    /// are issued either way — only the compressing is opt-in.
    /// <para/>
    /// Defaulting this on turned out to be wrong in a way that is obvious in hindsight: an
    /// existing installation upgrading to this version would, on its very first launch,
    /// compress every day folder older than <see cref="ArchiveAfterDays"/> — months of an
    /// operator's data disappearing from the Recordings / Ratio Review lists (into archives,
    /// but still) with no one having asked for it. That happened on a development machine
    /// with three months of real data on it. An Engineer switches it on in the Configuration
    /// tab, having decided the retention policy is right for that fab.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Day folders older than this are compressed. 0 disables the age rule.</summary>
    public int ArchiveAfterDays { get; set; } = 30;

    /// <summary>
    /// Size of the whole data tree (archives included) above which archiving continues into
    /// newer folders. 0 disables the size rule.
    /// </summary>
    public double MaxTotalSizeGB { get; set; } = 20;

    /// <summary>
    /// Newest days never archived, whatever the rules say — they are the ones an operator
    /// is most likely to be reviewing, and they may hold an open save session.
    /// </summary>
    public int MinKeepDays { get; set; } = 2;

    /// <summary>Free space on the data drive below which the app warns (percent of the drive).</summary>
    public double WarnFreeSpacePercent { get; set; } = 10;

    /// <summary>Free space below which the warning is raised to an error.</summary>
    public double CriticalFreeSpacePercent { get; set; } = 5;
}
