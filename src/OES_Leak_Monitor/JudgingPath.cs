using System;

namespace OES_Leak_Monitor;

/// <summary>
/// One way of judging a ratio's smoothed value: a Warn and an Alarm level, the
/// sustained-confirmation timer for each, and the latch a confirmed Alarm sets.
/// <para>
/// A <see cref="RatioMonitor"/> owns one path per judging method and forms the ratio's state
/// from all of them. Each path is judged independently: its own timers,
/// its own latch, its own idea of where Warn and Alarm sit. What the levels <em>are</em> — a
/// fraction of the Golden Run baseline, a rise above a rolling reference — is the caller's
/// business; the path only answers "has the value stayed above this for long enough".
/// </para>
/// </summary>
internal sealed class JudgingPath
{
    private DateTime? _aboveWarnSince, _aboveAlarmSince;

    /// <summary>True while a confirmed Alarm is held, until <see cref="Acknowledge"/>.</summary>
    public bool Latched { get; private set; }

    /// <summary>The level this path confirmed on its last <see cref="Judge"/>, before the latch
    /// is applied — so a latched path that has recovered reads Normal here.</summary>
    public RatioState Confirmed { get; private set; } = RatioState.Normal;

    /// <summary>What this path reports: Alarm while latched, otherwise <see cref="Confirmed"/>.</summary>
    public RatioState State => Latched ? RatioState.Alarm : Confirmed;

    /// <summary>
    /// Judges one frame. <paramref name="value"/> is compared with the two levels; a level counts
    /// once it has been continuously reached for <paramref name="confirmSeconds"/>. A confirmed
    /// Alarm latches unless <paramref name="suppressLatch"/> is set.
    /// </summary>
    public RatioState Judge(double value, double warn, double alarm, DateTime ts,
                            double confirmSeconds, bool suppressLatch)
    {
        if (value >= warn) _aboveWarnSince ??= ts; else _aboveWarnSince = null;
        if (value >= alarm) _aboveAlarmSince ??= ts; else _aboveAlarmSince = null;

        double confirm = Math.Max(0, confirmSeconds);
        var confirmed = RatioState.Normal;
        if (_aboveAlarmSince is { } a && (ts - a).TotalSeconds >= confirm)
            confirmed = RatioState.Alarm;
        else if (_aboveWarnSince is { } w && (ts - w).TotalSeconds >= confirm)
            confirmed = RatioState.Warning;

        Confirmed = confirmed;
        if (confirmed == RatioState.Alarm && !suppressLatch) Latched = true;
        return State;
    }

    /// <summary>Drops both confirmation timers — an interruption (plasma off, low signal, a
    /// reset) must not let time spent above a level before it count towards confirming after it.
    /// The latch is untouched.</summary>
    public void ResetTimers()
    {
        _aboveWarnSince = _aboveAlarmSince = null;
        Confirmed = RatioState.Normal;
    }

    public void Acknowledge() => Latched = false;

    /// <summary>Re-arms a latch carried over from a monitor this one replaced.</summary>
    public void RestoreLatch() => Latched = true;
}
