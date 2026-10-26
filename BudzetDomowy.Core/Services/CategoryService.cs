using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

public enum CategoryRemoval { Deleted, Archived }

public sealed class CategoryService
{
    private readonly EventRecorder _recorder;
    private readonly LocalReader _reader;

    public CategoryService(EventRecorder recorder, LocalReader reader)
    {
        _recorder = recorder;
        _reader = reader;
    }

    public Category Add(string name, Guid? parentId, CategoryType type, string color = "", string icon = "")
    {
        var siblings = _reader.GetCategories().Where(c => c.ParentId == parentId).ToList();
        var c = new Category
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            ParentId = parentId,
            Type = type,
            Color = color,
            Icon = icon,
            SortOrder = siblings.Count == 0 ? 0 : siblings.Max(s => s.SortOrder) + 1,
        };
        _recorder.Save(c);
        return c;
    }

    public void Save(Category c) => _recorder.Save(c with { Name = c.Name.Trim() });

    /// <summary>
    /// Deletes a category that is not used anywhere; a used one (transactions, subcategories, budgets, recurring
    /// entries, payees) is archived instead so history stays intact.
    /// </summary>
    public CategoryRemoval Remove(Guid id)
    {
        var all = _reader.GetCategories();
        var category = all.First(c => c.Id == id);
        var used = _reader.CountTransactionsInCategory(id) > 0
                   || all.Any(c => c.ParentId == id)
                   || _reader.GetBudgets().Any(b => b.CategoryId == id)
                   || _reader.GetRecurringTransactions().Any(r => r.CategoryId == id)
                   || _reader.GetPayees().Any(p => p.CategoryId == id);
        if (used)
        {
            _recorder.Save(category with { IsArchived = true });
            return CategoryRemoval.Archived;
        }

        _recorder.Delete(EntityTypes.Category, id);
        foreach (var rule in _reader.GetCategoryRules().Where(r => r.CategoryId == id))
            _recorder.Delete(EntityTypes.CategoryRule, rule.Id);
        return CategoryRemoval.Deleted;
    }

    /// <summary>Moves the category one place up or down among its siblings.</summary>
    public void Move(Guid id, int direction)
    {
        var all = _reader.GetCategories();
        var c = all.First(x => x.Id == id);
        var siblings = all.Where(x => x.ParentId == c.ParentId)
                          .OrderBy(x => x.SortOrder).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var index = siblings.FindIndex(x => x.Id == id);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count) return;

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            if (siblings[i].SortOrder != i) _recorder.Save(siblings[i] with { SortOrder = i });
    }

    /// <summary>Adds the default categories when there are none (first run of the very first device).</summary>
    public bool SeedIfEmpty()
    {
        if (_reader.GetCategories().Count > 0) return false;
        foreach (var c in DefaultCategories.Create()) _recorder.Save(c);
        return true;
    }
}
