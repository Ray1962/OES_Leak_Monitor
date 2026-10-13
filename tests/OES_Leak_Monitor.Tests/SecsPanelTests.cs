using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The SECS tab's traffic list follows its newest line. That is the one piece of the SECS
/// interface with a window on it, and the one that took the whole app down during a
/// commissioning run against Test_SECS.
/// </summary>
public class SecsPanelTests
{
    /// <summary>
    /// The panel hears about a new line before the ListBox's own collection view does — it
    /// subscribed first, when the DataContext arrived. Scrolling from inside that notification
    /// forces a layout pass against a generator that has not been told about the item yet. With
    /// one line at a time nothing is waiting to be laid out and it gets away with it; with two
    /// in a row the first has left the list dirty, the pass really runs, and WPF throws
    /// "ItemsControl is inconsistent with its items source". Unhandled on the dispatcher, that
    /// ends the process — on the first S1F1 a host sends with the tab open.
    /// </summary>
    [Fact]
    public void TwoTrafficLinesInARow_WithTheTabShowing_DoNotThrow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var folder = Path.Combine(Path.GetTempPath(), "oes-secs-panel-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var bridge = new SecsBridge(null, folder, folder,
                    () => new SecsBridge.AcquisitionInfo(120f, 4u));
                var vm = new SecsViewModel(bridge, new SecsSettings(), _ => { });

                var window = new Window
                {
                    Width = 900,
                    Height = 900,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };
                // Wired as MainWindow wires it: the panel sits in a tab that is not the selected
                // one and takes its DataContext from a binding.
                var panel = new SecsPanel();
                panel.SetBinding(FrameworkElement.DataContextProperty, new System.Windows.Data.Binding("Secs"));
                var secsTab = new System.Windows.Controls.TabItem { Header = "SECS", Content = panel };
                var tabs = new System.Windows.Controls.TabControl();
                tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "Monitor" });
                tabs.Items.Add(secsTab);
                window.DataContext = new { Secs = vm };
                window.Content = tabs;
                window.Show();
                Pump();

                // The interface starts with the app, so its start-up lines are already there by
                // the time anyone opens the tab.
                for (var i = 0; i < 3; i++)
                {
                    vm.Traffic.Add($"15:21:30.{i:000}  [CFG] line {i}");
                    Pump();
                }
                secsTab.IsSelected = true;
                Pump();

                // A message and its reply are logged in the same millisecond, so the second
                // line arrives before the layout pass the first one asked for has run.
                for (var i = 0; i < 20; i++)
                {
                    vm.Traffic.Add($"15:21:51.948  <-- 'S1F1' W {i}");
                    vm.Traffic.Add($"15:21:51.948  --> 'S1F2' OnLineData {i}");
                    Pump();
                }

                // Not crashing by no longer scrolling would be the wrong fix: with far more
                // lines than fit, the newest one still has to be on screen.
                for (var i = 0; i < 200; i++)
                {
                    vm.Traffic.Add($"15:22:00.000  [EQP] filler {i}");
                }
                Pump();
                var list = (System.Windows.Controls.ListBox)panel.FindName("TrafficList");
                var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
                Assert.True(last != null, "the newest traffic line should be scrolled into view");

                window.Close();
                Pump();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
