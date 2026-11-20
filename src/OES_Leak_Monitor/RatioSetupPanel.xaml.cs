using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OES_Leak_Monitor;

public partial class RatioSetupPanel : UserControl
{
    // Drives the line preview's live refresh, about once a second (#112) — only while the tab
    // is on screen, so a hidden preview costs nothing.
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LinePreviewViewModel? _preview;

    public RatioSetupPanel()
    {
        InitializeComponent();
        _previewTimer.Tick += (_, _) => _preview?.Tick();
        Loaded += (_, _) => _previewTimer.Start();
        Unloaded += (_, _) => _previewTimer.Stop();
        DataContextChanged += (_, _) =>
        {
            if (_preview is not null) _preview.LoadRecordingRequested -= OnLoadRecording;
            _preview = (DataContext as RatioSetupViewModel)?.Preview;
            if (_preview is not null) _preview.LoadRecordingRequested += OnLoadRecording;
        };
    }

    private void OnLoadRecording(object? sender, EventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Preview on a recorded spectrum",
            Filter = "Spectrum recordings (P_OES1_*.csv; *.zip)|P_OES1_*.csv;*.zip|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _preview?.Load(dlg.FileName);
    }
}
