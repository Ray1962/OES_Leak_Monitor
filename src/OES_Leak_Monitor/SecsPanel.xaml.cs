using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OES_Leak_Monitor;

public partial class SecsPanel : UserControl
{
    public SecsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => FollowTraffic();
    }

    private bool _scrollPending;

    /// <summary>
    /// Keeps the newest traffic line in view. A log you have to scroll to the bottom of after
    /// every message is one nobody watches while debugging a connection.
    /// </summary>
    /// <remarks>
    /// The scroll is posted, never done inside the notification. This handler subscribes when
    /// the DataContext arrives, which is before the ListBox's own collection view does, so it
    /// hears about a new line first — while the item generator still counts one fewer.
    /// <c>ScrollIntoView</c> forces a layout pass, and if the previous line left the list
    /// waiting for one (a message and its reply are logged in the same millisecond) the
    /// generator finds the mismatch and throws "ItemsControl is inconsistent with its items
    /// source" on the dispatcher, which ends the process.
    /// </remarks>
    private void FollowTraffic()
    {
        if (DataContext is not SecsViewModel vm)
        {
            return;
        }
        ((INotifyCollectionChanged)vm.Traffic).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending)
            {
                return;
            }
            _scrollPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _scrollPending = false;
                if (TrafficList.Items.Count > 0)
                {
                    TrafficList.ScrollIntoView(TrafficList.Items[TrafficList.Items.Count - 1]);
                }
            });
        };
    }
}
