using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;

namespace OES_Leak_Monitor;

/// <summary>
/// Baseline Builder tab. The only thing here that is not binding is the drag: choosing the steady
/// segment is a gesture over the trace, not a number typed into a box — the operator has to see
/// what they are choosing. The boxes stay, because a window read off one recording often wants to be
/// typed exactly into the next.
/// </summary>
public partial class BaselineBuilderPanel : UserControl
{
    private bool _dragging;
    private double _dragFrom;

    public BaselineBuilderPanel()
    {
        InitializeComponent();
        TracePlot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
        TracePlot.PreviewMouseMove += OnPlotMouseMove;
        TracePlot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
    }

    private BaselineBuilderViewModel? Vm => DataContext as BaselineBuilderViewModel;

    /// <summary>Data-space X under the pointer, or null when there is no plot to speak of.</summary>
    private double? DataX(MouseEventArgs e)
    {
        var model = TracePlot.ActualModel;
        if (model is null) return null;
        Axis? bottom = null;
        foreach (var a in model.Axes)
            if (a.Position == AxisPosition.Bottom) { bottom = a; break; }
        if (bottom is null) return null;
        var p = e.GetPosition(TracePlot);
        return bottom.InverseTransform(p.X);
    }

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.SelectedFile?.Scan is null) return;
        if (DataX(e) is not { } x) return;
        _dragging = true;
        _dragFrom = x;
        TracePlot.CaptureMouse();
        e.Handled = true;      // left-drag is window selection here, not pan
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (DataX(e) is not { } x) return;
        Vm?.SetWindow(_dragFrom, x);
        e.Handled = true;
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        TracePlot.ReleaseMouseCapture();
        if (DataX(e) is { } x) Vm?.SetWindow(_dragFrom, x);
        e.Handled = true;
    }
}
