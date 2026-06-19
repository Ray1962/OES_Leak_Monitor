using System.Windows.Controls;

namespace OES_Leak_Monitor;

/// <summary>Leak Calibration tab: capture calibration points and fit the leak-rate curve.
/// All behavior lives in <see cref="LeakCalibrationViewModel"/>.</summary>
public partial class LeakCalibrationPanel : UserControl
{
    public LeakCalibrationPanel() => InitializeComponent();
}
