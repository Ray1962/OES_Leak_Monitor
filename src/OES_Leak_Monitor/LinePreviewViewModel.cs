using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Aqst.OesSpectrometer.Models;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace OES_Leak_Monitor;

/// <summary>
/// The Ratio Setup line preview (#112): the selected ratio's numerator and denominator windows
/// drawn on one spectrum frame — the latest live frame (#122) or one picked from a recording
/// (#123) — following unsaved edits. What it computes comes from <see cref="LinePreview"/>; this
/// class only chooses the frame and draws. Read-only for every role: it changes nothing.
/// </summary>
public sealed class LinePreviewViewModel : INotifyPropertyChanged
{
    private readonly Func<IEnumerable<WavelengthCorrection>?> _corrections;
    private RatioEditViewModel? _row;

    // The newest live frame, written on the acquisition thread, read on the UI thread.
    private volatile SpectrumSample? _live;
    private SpectrumSample? _shownLive;

    private float[]? _wl, _y;

    public LinePreviewViewModel(Func<IEnumerable<WavelengthCorrection>?> corrections)
    {
        _corrections = corrections;
        NumeratorPlot = NewPlot();
        DenominatorPlot = NewPlot();
        FreezeCommand = new RelayCommand(() => IsFrozen = !IsFrozen, () => IsLive);
        UseLiveCommand = new RelayCommand(UseLive, () => !IsLive);
        LoadRecordingCommand = new RelayCommand(() => LoadRecordingRequested?.Invoke(this, EventArgs.Empty));
        Redraw();
    }

    public PlotModel NumeratorPlot { get; }
    public PlotModel DenominatorPlot { get; }

    public string NumeratorCaption { get; private set; } = "";
    public string DenominatorCaption { get; private set; } = "";
    public string NumeratorWarnings { get; private set; } = "";
    public string DenominatorWarnings { get; private set; } = "";
    public bool ShowDenominator { get; private set; }

    /// <summary>"Live" or the recording's name and the chosen frame's time.</summary>
    public string SourceText { get; private set; } = "";

    public RelayCommand FreezeCommand { get; }
    public RelayCommand UseLiveCommand { get; }
    public RelayCommand LoadRecordingCommand { get; }

    /// <summary>Raised by <see cref="LoadRecordingCommand"/>; the view shows the file picker
    /// and calls <see cref="LoadAsync"/>.</summary>
    public event EventHandler? LoadRecordingRequested;

    private bool _isFrozen;
    public bool IsFrozen
    {
        get => _isFrozen;
        set { if (_isFrozen == value) return; _isFrozen = value; Raise(); Raise(nameof(FreezeText)); if (!value) Tick(); }
    }

    public string FreezeText => IsFrozen ? "Resume" : "Freeze";

    public bool IsLive => _recording is null;

    // --- the row -----------------------------------------------------------------

    /// <summary>Follows the selected ratio and every edit made to it.</summary>
    public void SetRow(RatioEditViewModel? row)
    {
        if (_row is not null) _row.PropertyChanged -= OnRowChanged;
        _row = row;
        if (_row is not null) _row.PropertyChanged += OnRowChanged;
        Redraw();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Redraw();

    // --- live frames ---------------------------------------------------------------

    /// <summary>Called for every effective frame, on the acquisition thread. Only keeps a
    /// reference; drawing happens on <see cref="Tick"/>.</summary>
    public void OnLiveSpectrum(SpectrumSample sample) => _live = sample;

    private double? _pitchNm;
    /// <summary>Pixel pitch of the spectrum shown (or the newest live one), nm; null before
    /// any spectrum. Ratio Setup sizes new windows and counts pixels with it (#125).</summary>
    public double? PitchNm => _pitchNm ?? LineWindowAdvice.PitchOf(_live?.Wavelengths);

    /// <summary>Raised when the shown spectrum's pitch changes.</summary>
    public event EventHandler? PitchChanged;

    private void UpdatePitch()
    {
        var p = LineWindowAdvice.PitchOf(_wl);
        if (p is null || p == _pitchNm) return;
        _pitchNm = p;
        PitchChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Once a second from the view: draws the newest live frame unless frozen or a
    /// recording is shown.</summary>
    public void Tick()
    {
        if (!IsLive || IsFrozen) return;
        var s = _live;
        if (s is null || ReferenceEquals(s, _shownLive)) return;
        _shownLive = s;
        _wl = s.Wavelengths; _y = s.Intensities;
        SetSource($"Live · {s.Timestamp:HH:mm:ss}");
        Redraw();
    }

    private void UseLive()
    {
        _recording = null; _recordingName = "";
        _shownLive = null; _wl = null; _y = null;
        Raise(nameof(IsLive)); Raise(nameof(HasRecording)); Raise(nameof(FrameCount)); Raise(nameof(MaxFrameIndex));
        FreezeCommand.RaiseCanExecuteChanged(); UseLiveCommand.RaiseCanExecuteChanged();
        SetSource("Live · waiting for a frame");
        Tick();
        Redraw();
    }

    // --- recorded frames (#123) ------------------------------------------------------

    private FullRecording? _recording;
    private string _recordingName = "";
    private DateTime _recordingStart;

    public bool HasRecording => _recording is not null;
    public int FrameCount => _recording is null ? 0 : _recording.FrameCount;
    public int MaxFrameIndex => Math.Max(0, FrameCount - 1);

    /// <summary>The recordings inside a chosen zip (or the one chosen CSV).</summary>
    public ObservableCollection<Recording> RecordingChoices { get; } = new();

    private Recording? _selectedRecording;
    public Recording? SelectedRecording
    {
        get => _selectedRecording;
        set
        {
            if (ReferenceEquals(_selectedRecording, value)) return;
            _selectedRecording = value; Raise();
            if (value is not null) _ = OpenAsync(value);
        }
    }

    private int _frameIndex;
    public int FrameIndex
    {
        get => _frameIndex;
        set
        {
            int v = Math.Clamp(value, 0, Math.Max(0, FrameCount - 1));
            if (_frameIndex == v) return;
            _frameIndex = v; Raise();
            ShowRecordedFrame();
        }
    }

    /// <summary>Lists the recordings in a chosen file — a <c>P_OES1_*.csv</c> or a zipped day
    /// folder — and opens the first. A file that is neither leaves the preview as it was.</summary>
    public void Load(string path)
    {
        var found = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Recording.FromArchive(path).Where(r => r.FileName.StartsWith("P_OES1_", StringComparison.OrdinalIgnoreCase)).ToList()
            : Recording.TryParse(path) is { } one ? new List<Recording> { one } : new List<Recording>();
        RecordingChoices.Clear();
        foreach (var x in found.OrderBy(x => x.SessionStart)) RecordingChoices.Add(x);
        if (RecordingChoices.Count == 0) { SetSource($"{Path.GetFileName(path)}: no P_OES1 recording found"); return; }
        _selectedRecording = null;
        SelectedRecording = RecordingChoices[0];
    }

    private async Task OpenAsync(Recording r)
    {
        SetSource($"Reading {r.FileName}…");
        try
        {
            var parsed = await Task.Run(() =>
            {
                using var reader = r.OpenText();
                return RecordingCsvParser.ReadFull(reader, System.Threading.CancellationToken.None);
            });
            if (parsed is null || parsed.FrameCount == 0) { SetSource($"{r.FileName}: not a full-spectrum recording"); return; }
            _recording = parsed;
            _recordingName = r.FileName;
            _recordingStart = r.SessionStart;
            Raise(nameof(IsLive)); Raise(nameof(HasRecording)); Raise(nameof(FrameCount)); Raise(nameof(MaxFrameIndex));
            FreezeCommand.RaiseCanExecuteChanged(); UseLiveCommand.RaiseCanExecuteChanged();
            _frameIndex = -1;
            FrameIndex = 0;
        }
        catch (Exception ex)
        {
            SetSource($"{r.FileName}: {ex.Message}");
        }
    }

    private void ShowRecordedFrame()
    {
        if (_recording is null) return;
        _wl = _recording.Wavelengths;
        _y = _recording.Intensities[_frameIndex];
        double el = _recording.ElapsedSec[_frameIndex];
        SetSource($"{_recordingName} · frame {_frameIndex + 1}/{FrameCount} · {_recordingStart.AddSeconds(el):HH:mm:ss} (+{el:0} s)");
        Redraw();
    }

    // --- drawing ---------------------------------------------------------------------

    private void Redraw()
    {
        UpdatePitch();
        var lookup = WavelengthCalibration.Build(_corrections());
        var num = _row is null ? null : WavelengthCalibration.Correct(_row.SignalRegionPreview, lookup);
        var den = _row?.ReferenceRegionPreview is { } d ? WavelengthCalibration.Correct(d, lookup) : null;

        (NumeratorCaption, NumeratorWarnings) = Draw(NumeratorPlot, num, "Monitored line");
        (DenominatorCaption, DenominatorWarnings) = Draw(DenominatorPlot, den, "Reference line");
        ShowDenominator = den is not null;
        Raise(nameof(NumeratorCaption)); Raise(nameof(NumeratorWarnings));
        Raise(nameof(DenominatorCaption)); Raise(nameof(DenominatorWarnings));
        Raise(nameof(ShowDenominator));
        if (SourceText.Length == 0) SetSource(IsLive ? "Live · waiting for a frame" : "");
    }

    private (string Caption, string Warnings) Draw(PlotModel plot, LineRegion? region, string role)
    {
        plot.Series.Clear();
        plot.Annotations.Clear();
        if (region is null) { plot.InvalidatePlot(true); return ("", ""); }

        var x = (LinearAxis)plot.Axes[0];
        if (_wl is null || _y is null || _wl.Length < 2)
        {
            plot.Title = $"{role}: {region.Label}";
            x.Minimum = region.CenterNm - 5; x.Maximum = region.CenterNm + 5;
            plot.InvalidatePlot(true);
            return ($"{region.Mode} · no spectrum yet", "");
        }

        var p = LinePreview.Compute(_wl, _y, region, SpectralLineCatalog.All);
        x.Minimum = p.ViewLo; x.Maximum = p.ViewHi;

        var line = new LineSeries { Color = OxyColor.FromRgb(0x33, 0x33, 0x33), StrokeThickness = 1.2 };
        int n = Math.Min(_wl.Length, _y.Length);
        for (int i = 0; i < n; i++)
            if (_wl[i] >= p.ViewLo && _wl[i] <= p.ViewHi) line.Points.Add(new DataPoint(_wl[i], _y[i]));
        plot.Series.Add(line);

        var w = p.Windows;
        plot.Annotations.Add(Band(w.SignalLo, w.SignalHi, OxyColor.FromAColor(70, OxyColor.FromRgb(0x1F, 0x7A, 0x3A)), "signal"));
        if (p.HasBaselineWindows)
        {
            var blue = OxyColor.FromAColor(60, OxyColor.FromRgb(0x2B, 0x6C, 0xB0));
            plot.Annotations.Add(Band(w.LeftLo, w.LeftHi, blue, "left"));
            plot.Annotations.Add(Band(w.RightLo, w.RightHi, blue, "right"));
        }
        if (p.SearchHi > p.SearchLo)
        {
            foreach (var at in new[] { p.SearchLo, p.SearchHi })
                plot.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Vertical, X = at, LineStyle = LineStyle.Dash,
                    Color = OxyColor.FromRgb(0x88, 0x88, 0x88), StrokeThickness = 1,
                });
        }
        plot.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical, X = region.CenterNm, LineStyle = LineStyle.Dot,
            Color = OxyColor.FromRgb(0x1F, 0x3A, 0x66), StrokeThickness = 1,
        });

        plot.Title = $"{role}: {region.Label}";
        plot.InvalidatePlot(true);

        string reading = p.Reading.HasValue ? $"reading {p.Reading.Value:0.###}" : "no reading";
        string shift = Math.Abs(p.ShiftNm) > 1e-9 ? $" · re-centred {p.ShiftNm:+0.00;-0.00} nm" : "";
        return ($"{region.Mode} · {reading}{shift}", string.Join("\n", p.Warnings.Select(v => "⚠ " + v.Text)));
    }

    private static RectangleAnnotation Band(double lo, double hi, OxyColor fill, string text) => new()
    {
        MinimumX = lo, MaximumX = hi, Fill = fill, Text = text,
        TextColor = OxyColor.FromRgb(0x44, 0x44, 0x44), FontSize = 9,
        TextVerticalAlignment = VerticalAlignment.Top,
    };

    private static PlotModel NewPlot()
    {
        var m = new PlotModel { TitleFontSize = 11, TitleFontWeight = 500, PlotMargins = new OxyThickness(50, 4, 8, 30) };
        m.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "nm", FontSize = 10, IsZoomEnabled = false, IsPanEnabled = false });
        m.Axes.Add(new LinearAxis { Position = AxisPosition.Left, FontSize = 10, MajorGridlineStyle = LineStyle.Dot, MajorGridlineColor = OxyColor.FromRgb(0xE5, 0xE5, 0xE5) });
        return m;
    }

    private void SetSource(string text) { SourceText = text; Raise(nameof(SourceText)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
