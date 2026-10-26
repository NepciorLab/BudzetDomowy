using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.App.Controls;

/// <summary>
/// Text box for a place (shop, service) that completes what is typed from the places known so far. The most popular place that
/// starts with the typed letters is written after the caret as selected text (Enter, Tab or Right accept it with the letters
/// of the place, Delete or Esc drop it, any other letter replaces it). The list below holds the matching places in alphabetical
/// order; Down and Up walk through it and the box follows. <see cref="Suggestions"/> must be ordered by popularity.
/// </summary>
public class PlaceBox : TextBox
{
    public static readonly DependencyProperty SuggestionsProperty = DependencyProperty.Register(
        nameof(Suggestions), typeof(IEnumerable), typeof(PlaceBox), new PropertyMetadata(null));

    private const int MaxListed = 15;

    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _busy;
    private bool _userEdit;
    private string _typed = "";
    private PlaceSuggestion? _completion;

    public IEnumerable? Suggestions
    {
        get => (IEnumerable?)GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    public PlaceBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));      // a derived control does not get the implicit TextBox style by itself
        _list = new ListBox
        {
            Focusable = false,
            DisplayMemberPath = nameof(PlaceSuggestion.Name),
            BorderThickness = new Thickness(0),
            MaxHeight = 240,
        };
        _list.PreviewMouseLeftButtonUp += OnListClick;
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            Child = _list,
            Focusable = false,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseMediumLowBrush");
        _popup = new Popup
        {
            Child = frame,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = false,
            Focusable = false,
        };
        TextChanged += OnTextChanged;
        LostKeyboardFocus += (_, _) =>
        {
            Accept();
            Close();
        };
        Unloaded += (_, _) => Close();
    }

    /// <summary>A completion or the list of offers is on show (Esc then only closes it).</summary>
    public bool IsSuggesting => _popup.IsOpen || _completion is not null;

    private IReadOnlyList<PlaceSuggestion> All() => Suggestions?.Cast<PlaceSuggestion>().ToList() ?? [];

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        MarkUserEdit();
        base.OnPreviewTextInput(e);
    }

    /// <summary>Only what the user does (typing, Backspace, Delete) starts a completion; the box changed from outside does not.</summary>
    private void MarkUserEdit()
    {
        _userEdit = true;
        Dispatcher.BeginInvoke(() => _userEdit = false, DispatcherPriority.Input);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_busy || !_userEdit) return;
        _completion = null;
        _typed = Text;

        var matches = PlaceSuggestionService.StartingWith(All(), Text, MaxListed);
        if (matches.Count == 0)
        {
            Close();
            return;
        }

        var best = matches[0];                                        // the most popular one
        _list.ItemsSource = matches.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _list.SelectedItem = null;
        _popup.MinWidth = ActualWidth;
        _popup.IsOpen = true;

        if (e.Changes.Any(c => c.AddedLength > 0 && c.Offset + c.AddedLength == Text.Length)) ShowCompletion(best);      // letters added at the end
    }

    /// <summary>Writes the rest of the place after the typed letters, selected, and marks it in the list.</summary>
    private void ShowCompletion(PlaceSuggestion item)
    {
        var typed = _typed;
        if (item.Name.Length <= typed.Length) return;
        _busy = true;
        try
        {
            Text = typed + item.Name[typed.Length..];
            Select(typed.Length, Text.Length - typed.Length);
        }
        finally
        {
            _busy = false;
        }
        _completion = item;
        _list.SelectedItem = _list.Items.Cast<PlaceSuggestion>().FirstOrDefault(i => i.Name == item.Name);
        if (_list.SelectedItem is not null) _list.ScrollIntoView(_list.SelectedItem);
    }

    /// <summary>Takes the shown completion: the whole place, with the capital letters it has.</summary>
    public void Accept()
    {
        if (_completion is { } c && SelectionLength > 0 && SelectionStart + SelectionLength == Text.Length && Text.Length == c.Name.Length)
            SetText(c.Name);
        _completion = null;
        Close();
    }

    /// <summary>Drops the shown completion and leaves what was typed.</summary>
    private void DropCompletion()
    {
        if (_completion is not null) SetText(_typed);
        _completion = null;
    }

    private void SetText(string text)
    {
        _busy = true;
        try
        {
            Text = text;
            CaretIndex = Text.Length;
        }
        finally
        {
            _busy = false;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete) MarkUserEdit();

        if (_popup.IsOpen || _completion is not null)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Up when _popup.IsOpen && _list.Items.Count > 0:
                    Walk(e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    DropCompletion();
                    Close();
                    e.Handled = true;
                    return;
                case Key.Right or Key.End when _completion is not null && SelectionLength > 0:
                    Accept();
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab:
                    Accept();                                         // the key itself still does its usual job (next field)
                    break;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Down and Up: the next or previous place of the list is written after the caret (above the first one: only what was typed).</summary>
    private void Walk(int step)
    {
        var index = Math.Clamp(_list.SelectedIndex + step, -1, _list.Items.Count - 1);
        if (index < 0)
        {
            DropCompletion();
            _list.SelectedItem = null;
            return;
        }

        ShowCompletion((PlaceSuggestion)_list.Items[index]);
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var item = e.OriginalSource is DependencyObject source ? ItemsControl.ContainerFromElement(_list, source) as ListBoxItem : null;
        if (item?.Content is PlaceSuggestion chosen)
        {
            SetText(chosen.Name);
            _completion = null;
            Close();
            Focus();
            e.Handled = true;
        }
    }

    private void Close() => _popup.IsOpen = false;
}
