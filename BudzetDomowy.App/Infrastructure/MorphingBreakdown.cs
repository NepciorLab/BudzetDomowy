using System.Collections.ObjectModel;
using System.Windows.Threading;
using BudzetDomowy.App.Controls;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// The expenses by category for the ring and the bars, both kept alive from one month to the next: each category is one
/// <see cref="ChartItem"/> that both charts show, so showing another month only changes the amounts. The ring stays where it is, its
/// borders move to the new shares, and the bars grow or shrink in place; nothing is drawn again from the centre or from the bottom.
/// Items that are gone shrink to nothing and are removed as soon as that has happened; new ones are added at the end. Only the very
/// first drawing is animated from the beginning.
/// </summary>
public sealed class MorphingBreakdown
{
    private sealed class Slice
    {
        public required string Key { get; init; }
        public required ChartItem Item { get; init; }
        public bool Gone { get; set; }
    }

    private static readonly TimeSpan GhostLifetime = TimeSpan.FromMilliseconds(800);

    private readonly List<Slice> _slices = [];
    private DispatcherTimer? _cleanup;

    /// <summary>The items of the ring and of the bars; the instance never changes, its content does.</summary>
    public ObservableCollection<ChartItem> Items { get; } = [];

    /// <param name="slices">Key (what makes a slice the same slice in another month), the name, the colour and the amount.</param>
    public void Update(IEnumerable<(string Key, string Name, string Color, decimal Amount)> slices)
    {
        var target = slices.ToList();
        var keys = target.Select(s => s.Key).ToHashSet();

        foreach (var gone in _slices.Where(s => !keys.Contains(s.Key)).ToList())
        {
            if (gone.Gone)
            {
                Remove(gone);
            }
            else
            {
                gone.Gone = true;
                gone.Item.Amount = 0;                                          // shrinks away and is removed a moment later
            }
        }

        foreach (var (key, name, color, amount) in target)
        {
            var slice = _slices.FirstOrDefault(s => s.Key == key);
            if (slice is null)
            {
                slice = new Slice { Key = key, Item = new ChartItem { Name = name, Color = color, Amount = (double)amount } };
                _slices.Add(slice);
                Items.Add(slice.Item);
                continue;
            }

            slice.Gone = false;
            slice.Item.Name = name;
            slice.Item.Color = color;
            slice.Item.Amount = (double)amount;
        }

        if (_slices.Any(s => s.Gone)) ScheduleCleanup();
    }

    private void Remove(Slice slice)
    {
        _slices.Remove(slice);
        Items.Remove(slice.Item);
    }

    /// <summary>Removes the items that have shrunk to nothing.</summary>
    public void RemoveGone()
    {
        foreach (var s in _slices.Where(s => s.Gone).ToList()) Remove(s);
    }

    private void ScheduleCleanup()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null) return;
        _cleanup ??= new DispatcherTimer(GhostLifetime, DispatcherPriority.Background, (_, _) =>
        {
            _cleanup?.Stop();
            RemoveGone();
        }, dispatcher);
        _cleanup.Stop();                                                       // every change gives the shrinking another moment
        _cleanup.Start();
    }
}
