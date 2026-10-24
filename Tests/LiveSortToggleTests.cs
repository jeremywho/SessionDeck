using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>The session list is a live-sorted, live-grouped view; pausing it for a drag must not leave it deaf afterwards.</summary>
public class LiveSortToggleTests
{
    sealed class Item : INotifyPropertyChanged
    {
        public string Group { get; }
        public string Name { get; }
        int _priority;
        public Item(string group, string name, int priority) { Group = group; Name = name; _priority = priority; }
        public int Priority
        {
            get => _priority;
            set { _priority = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    static (ObservableCollection<Item> Items, ListCollectionView View) Build()
    {
        var items = new ObservableCollection<Item>
        {
            new("side", "chess database", 4), new("side", "flashpad", 4), new("side", "local chess", 4), new("tr", "sweep", 4),
        };
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Item.Group)));
        view.IsLiveGrouping = true;
        view.LiveGroupingProperties.Add(nameof(Item.Group));
        view.SortDescriptions.Add(new SortDescription(nameof(Item.Group), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(Item.Priority), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(Item.Name), ListSortDirection.Ascending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(Item.Priority));
        return (items, view);
    }

    /// <summary>Live shaping is applied by the dispatcher, not inline; run everything queued on it before looking.</summary>
    static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    static string[] Order(ListCollectionView view) { Pump(); return view.Cast<Item>().Select(i => i.Name).ToArray(); }

    [Fact]
    public void A_priority_change_moves_the_row_within_its_group()
    {
        var (items, view) = Build();
        items[2].Priority = 2;
        Assert.Equal(new[] { "local chess", "chess database", "flashpad", "sweep" }, Order(view));
    }

    [Fact]
    public void Pausing_live_shaping_for_a_drag_and_resuming_it_leaves_the_view_live()
    {
        var (items, view) = Build();
        view.IsLiveSorting = false;
        view.IsLiveGrouping = false;
        items[0].Priority = 2;
        view.IsLiveSorting = true;
        view.IsLiveGrouping = true;
        view.Refresh();
        Assert.Equal(new[] { "chess database", "flashpad", "local chess", "sweep" }, Order(view));

        items[2].Priority = 2;
        Assert.Equal(new[] { "chess database", "local chess", "flashpad", "sweep" }, Order(view));
    }
}
