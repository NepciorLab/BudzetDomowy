using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BudzetDomowy.App.Controls;

/// <summary>
/// Editable combo box that filters its list while the user types. Tab, Enter or leaving the control accepts the
/// exact match or the first filtered item; text that matches nothing is reverted. Bind the list to <see cref="Choices"/>
/// (the control keeps a private view, so several boxes can share one list without sharing the filter) and the value
/// to <see cref="ComboBox.SelectedValue"/> with SelectedValuePath, which survives list refreshes.
/// </summary>
public class FilterComboBox : ComboBox
{
    public static readonly DependencyProperty ChoicesProperty = DependencyProperty.Register(
        nameof(Choices), typeof(IEnumerable), typeof(FilterComboBox),
        new PropertyMetadata(null, (d, e) => ((FilterComboBox)d).OnChoicesChanged((IEnumerable?)e.NewValue)));

    private TextBox? _text;
    private ListCollectionView? _view;
    private List<object> _all = [];

    public IEnumerable? Choices
    {
        get => (IEnumerable?)GetValue(ChoicesProperty);
        set => SetValue(ChoicesProperty, value);
    }

    public FilterComboBox()
    {
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
        // Otherwise the collection view's "current item" (the first one) is selected whenever the list is rebuilt.
        IsSynchronizedWithCurrentItem = false;
        SetResourceReference(StyleProperty, typeof(ComboBox));
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!IsKeyboardFocusWithin) Commit();
        };
        DropDownOpened += (_, _) => KeepCaretAtEndWhileTyping();
        DropDownClosed += (_, _) => ResetFilter();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_text is not null) _text.TextChanged -= OnTextChanged;
        _text = GetTemplateChild("PART_EditableTextBox") as TextBox;
        if (_text is not null) _text.TextChanged += OnTextChanged;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Resolve what was typed before Enter/Tab is acted on by a parent (e.g. a DataGrid committing its cell).
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab) Commit();
        base.OnPreviewKeyDown(e);
    }

    private void OnChoicesChanged(IEnumerable? choices)
    {
        _all = choices?.Cast<object>().ToList() ?? [];
        _view = new ListCollectionView(_all);
        ItemsSource = _view;
    }

    private string Display(object? item)
    {
        if (item is null) return "";
        if (!string.IsNullOrEmpty(DisplayMemberPath))
            return item.GetType().GetProperty(DisplayMemberPath)?.GetValue(item)?.ToString() ?? "";
        return item.ToString() ?? "";
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view is null || _text is null) return;
        var text = _text.Text;

        // Text produced by a selection (click, arrow keys, binding) must not re-filter the list.
        if (SelectedItem is not null && string.Equals(Display(SelectedItem), text, StringComparison.Ordinal)) return;

        var needle = text.Trim();
        _view.Filter = needle.Length == 0 ? null : o => Display(o).Contains(needle, StringComparison.CurrentCultureIgnoreCase);
        if (_text.IsKeyboardFocused && needle.Length > 0 && !IsDropDownOpen)
        {
            IsDropDownOpen = true;
            KeepCaretAtEndWhileTyping();
        }
    }

    /// <summary>Opening the list selects the text box content; the next typed letter would replace what was typed.</summary>
    private void KeepCaretAtEndWhileTyping()
    {
        if (_text is null || _view?.Filter is null) return;
        void Restore()
        {
            if (_text.IsKeyboardFocused && _view?.Filter is not null) _text.Select(_text.Text.Length, 0);
        }
        Restore();
        Dispatcher.BeginInvoke(Restore, System.Windows.Threading.DispatcherPriority.Send);
        Dispatcher.BeginInvoke(Restore, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ResetFilter()
    {
        if (_view is not null && _view.Filter is not null) _view.Filter = null;
    }

    /// <summary>Resolves what is typed into a selection; call before reading the value.</summary>
    public void Commit()
    {
        if (_text is null) return;
        var text = _text.Text.Trim();

        if (text.Length == 0)
        {
            if (SelectedItem is not null) SelectedItem = null;
        }
        else if (SelectedItem is null || !string.Equals(Display(SelectedItem), text, StringComparison.Ordinal))
        {
            var match = _all.FirstOrDefault(o => string.Equals(Display(o), text, StringComparison.CurrentCultureIgnoreCase))
                        ?? _view?.Cast<object>().FirstOrDefault();
            if (match is not null) SelectedItem = match;
            else _text.Text = Display(SelectedItem); // nothing matches: go back to the last valid value
        }

        ResetFilter();
        IsDropDownOpen = false;
    }

    /// <summary>Clears the selection and whatever has been typed.</summary>
    public void Reset()
    {
        SelectedItem = null;
        if (_text is not null) _text.Text = "";
        ResetFilter();
        IsDropDownOpen = false;
    }
}
