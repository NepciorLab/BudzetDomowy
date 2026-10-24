using System.Collections.ObjectModel;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public sealed partial class CategoryNode : ObservableObject
{
    public Category Model { get; }
    public ObservableCollection<CategoryNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;

    public CategoryNode(Category model) => Model = model;

    public string Title => Model.IsArchived ? $"{Model.Name} {Res.Get("ArchivedSuffix")}" : Model.Name;
    public string Icon => Model.Icon;
    public string Color => Model.Color;
    public double TitleOpacity => Model.IsArchived ? 0.5 : 1.0;
}

public sealed record ParentOption(Guid Key, string Name);

public sealed record CategoryTypeOption(CategoryType Type, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class CategoriesViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly HashSet<Guid> _collapsed = [];
    private bool _loading;

    public ObservableCollection<CategoryNode> Roots { get; } = [];

    public IReadOnlyList<CategoryTypeOption> Types { get; } =
    [
        new(CategoryType.Expense, Res.Get("CategoryTypeExpense")),
        new(CategoryType.Income, Res.Get("CategoryTypeIncome")),
        new(CategoryType.Both, Res.Get("CategoryTypeBoth")),
    ];

    public IReadOnlyList<string> Palette { get; } =
    [
        "#E53935", "#EC407A", "#8E24AA", "#5C6BC0", "#1E88E5", "#26C6DA", "#00ACC1", "#43A047",
        "#7CB342", "#FFB300", "#FB8C00", "#8D6E63", "#546E7A", "#78909C", "#D4AF37", "#6D4C41",
    ];

    /// <summary>Pictures to choose from; any other emoji can still be typed in.</summary>
    public IReadOnlyList<string> IconChoices { get; } =
    [
        "🛒", "🍞", "🥩", "🥦", "🍎", "🥛", "🍰", "🍕", "🍔", "☕", "🍷", "🍺",
        "🏠", "🛋", "🔧", "💡", "🚿", "🧹", "🧴", "🛏", "📺", "🔌",
        "🚗", "⛽", "🚌", "🚆", "🚲", "✈", "🚕", "🅿",
        "💊", "🏥", "🦷", "👓", "💉", "🧸", "👶", "🎒", "📚", "🎓",
        "👕", "👗", "👟", "🧥", "💍", "👜",
        "🎬", "🎮", "🎵", "🎁", "🎉", "🏖", "⛷", "⚽", "🏊", "🎨", "📷", "🎫",
        "💼", "💰", "💵", "💳", "🏦", "📈", "🧾", "🏛",
        "📦", "📱", "💻", "🐶", "🐱", "🌱", "✂", "🔑", "⛪", "🕯",
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private CategoryNode? _selectedNode;
    [ObservableProperty] private IReadOnlyList<ParentOption> _parentChoices = [];

    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editIcon = "";
    [ObservableProperty] private string _editColor = "";
    [ObservableProperty] private CategoryTypeOption? _editType;
    [ObservableProperty] private Guid _editParentKey = Guid.Empty;
    [ObservableProperty] private bool _editArchived;
    [ObservableProperty] private string _message = "";

    public bool HasSelection => SelectedNode is not null;

    public CategoriesViewModel(BudgetCore core, DataHub hub, ToastService toast)
    {
        _core = core;
        _toast = toast;
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    partial void OnSelectedNodeChanged(CategoryNode? value)
    {
        if (_loading) return;
        LoadEditor(value);
    }

    private void LoadEditor(CategoryNode? node)
    {
        Message = "";
        if (node is null) return;
        var c = node.Model;
        EditName = c.Name;
        EditIcon = c.Icon;
        EditColor = c.Color;
        EditType = Types.First(t => t.Type == c.Type);
        EditArchived = c.IsArchived;

        var all = _core.Reader.GetCategories();
        var excluded = CategoryHelpers.WithDescendants(c.Id, all);
        var options = new List<ParentOption> { new(Guid.Empty, Res.Get("NoParent")) };
        options.AddRange(CategoryHelpers.Flatten(all, x => !excluded.Contains(x.Id)).Select(o => new ParentOption(o.Id, o.DisplayName)));
        ParentChoices = options;
        EditParentKey = c.ParentId ?? Guid.Empty;
    }

    public void Reload()
    {
        var selectedId = SelectedNode?.Model.Id;
        _loading = true;
        try
        {
            foreach (var n in Flatten(Roots)) if (!n.IsExpanded) _collapsed.Add(n.Model.Id); else _collapsed.Remove(n.Model.Id);

            var all = _core.Reader.GetCategories();
            var ids = all.Select(c => c.Id).ToHashSet();
            var byParent = all.ToLookup(c => c.ParentId is { } p && ids.Contains(p) ? p : Guid.Empty);

            CategoryNode Build(Category c)
            {
                var node = new CategoryNode(c) { IsExpanded = !_collapsed.Contains(c.Id) };
                foreach (var child in Ordered(byParent[c.Id])) node.Children.Add(Build(child));
                return node;
            }

            Roots.Clear();
            foreach (var c in Ordered(byParent[Guid.Empty])) Roots.Add(Build(c));

            var again = selectedId is { } id ? Flatten(Roots).FirstOrDefault(n => n.Model.Id == id) : null;
            if (again is not null) again.IsSelected = true;
            SelectedNode = again;
        }
        finally
        {
            _loading = false;
        }
        LoadEditor(SelectedNode);
    }

    private static IEnumerable<Category> Ordered(IEnumerable<Category> cs) =>
        cs.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);

    private static IEnumerable<CategoryNode> Flatten(IEnumerable<CategoryNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private void Select(Guid id)
    {
        Reload();
        var node = Flatten(Roots).FirstOrDefault(n => n.Model.Id == id);
        if (node is null) return;
        node.IsSelected = true;
        SelectedNode = node;
    }

    [RelayCommand]
    private void AddRoot()
    {
        var type = SelectedNode?.Model.Type ?? CategoryType.Expense;
        Run(() => Select(_core.Categories.Add(Res.Get("NewCategoryName"), null, type).Id));
    }

    [RelayCommand]
    private void AddChild()
    {
        if (SelectedNode is not { } parent) return;
        Run(() =>
        {
            parent.IsExpanded = true;
            Select(_core.Categories.Add(Res.Get("NewCategoryName"), parent.Model.Id, parent.Model.Type, parent.Model.Color).Id);
        });
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedNode is not { } node) return;
        var name = EditName.Trim();
        if (name.Length == 0)
        {
            Message = Res.Get("CategoryNameRequired");
            return;
        }

        var color = EditColor.Trim();
        if (color.Length > 0 && !IsHexColor(color))
        {
            Message = Res.Get("CategoryColorInvalid");
            return;
        }

        var parent = EditParentKey == Guid.Empty ? (Guid?)null : EditParentKey;
        var moved = parent != node.Model.ParentId;
        var updated = node.Model with
        {
            Name = name,
            Icon = EditIcon.Trim(),
            Color = color,
            Type = EditType?.Type ?? node.Model.Type,
            IsArchived = EditArchived,
            ParentId = parent,
        };
        if (moved)
        {
            var siblings = _core.Reader.GetCategories().Where(c => c.ParentId == parent).ToList();
            updated = updated with { SortOrder = siblings.Count == 0 ? 0 : siblings.Max(s => s.SortOrder) + 1 };
        }

        Run(() =>
        {
            _core.Categories.Save(updated);
            Select(updated.Id);
            Message = Res.Get("CategorySaved");
        });
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedNode is not { } node) return;
        var name = node.Model.Name;
        Run(() =>
        {
            var result = _core.Categories.Remove(node.Model.Id);
            SelectedNode = null;
            Reload();
            _toast.Show(Res.Format(result == CategoryRemoval.Deleted ? "CategoryDeleted" : "CategoryArchivedToast", name));
        });
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(+1);

    private void Move(int direction)
    {
        if (SelectedNode is not { } node) return;
        Run(() =>
        {
            _core.Categories.Move(node.Model.Id, direction);
            Select(node.Model.Id);
        });
    }

    [RelayCommand]
    private void PickIcon(string? icon)
    {
        if (icon is not null) EditIcon = icon;
    }

    [RelayCommand]
    private void PickColor(string? hex)
    {
        if (hex is not null) EditColor = hex;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (UpdateRequiredException) { Message = Res.Get("UpdateRequiredShort"); }
    }

    private static bool IsHexColor(string s) =>
        s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}
