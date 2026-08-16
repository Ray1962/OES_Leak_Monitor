using System.Collections.Specialized;
using System.Windows.Controls;

namespace OES_Leak_Monitor;

public partial class SecsPanel : UserControl
{
    public SecsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => FollowTraffic();
    }

    /// <summary>
    /// Keeps the newest traffic line in view. A log you have to scroll to the bottom of after
    /// every message is one nobody watches while debugging a connection.
    /// </summary>
    private void FollowTraffic()
    {
        if (DataContext is not SecsViewModel vm)
        {
            return;
        }
        ((INotifyCollectionChanged)vm.Traffic).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && TrafficList.Items.Count > 0)
            {
                TrafficList.ScrollIntoView(TrafficList.Items[TrafficList.Items.Count - 1]);
            }
        };
    }
}
