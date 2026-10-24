using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

public sealed record CategorySuggestion(Guid CategoryId, Guid? EventId, string Pattern);

/// <summary>
/// Learns "note text → category (and event)" from the user's decisions and suggests them later.
/// A rule's id is derived from its pattern, so two devices learning the same pattern update the same rule.
/// </summary>
public sealed class CategoryRuleService
{
    public const int MinPatternLength = 3;

    private readonly LocalReader _reader;
    private readonly EventRecorder _recorder;

    public CategoryRuleService(LocalReader reader, EventRecorder recorder)
    {
        _reader = reader;
        _recorder = recorder;
    }

    public static string Normalize(string? note) =>
        string.Join(' ', (note ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static Guid RuleId(string pattern) => DeterministicId.FromString("category-rule:" + pattern);

    /// <summary>Suggestion for a note: an exact pattern match, otherwise the longest pattern contained in the note.</summary>
    public CategorySuggestion? Suggest(string? note) => Suggest(note, _reader.GetCategoryRules());

    public static CategorySuggestion? Suggest(string? note, IReadOnlyList<CategoryRule> rules)
    {
        var text = Normalize(note);
        if (text.Length < MinPatternLength) return null;

        var best = rules.FirstOrDefault(r => r.Pattern == text)
                   ?? rules.Where(r => r.Pattern.Length >= MinPatternLength && text.Contains(r.Pattern, StringComparison.Ordinal))
                           .OrderByDescending(r => r.Pattern.Length).ThenByDescending(r => r.UseCount).FirstOrDefault();
        return best is null ? null : new CategorySuggestion(best.CategoryId, best.EventId, best.Pattern);
    }

    /// <summary>Records the user's decision. Repeating a decision strengthens the rule; a different one replaces it.</summary>
    public void Learn(string? note, Guid categoryId, Guid? eventId)
    {
        var pattern = Normalize(note);
        if (pattern.Length < MinPatternLength) return;

        var id = RuleId(pattern);
        var existing = _reader.GetCategoryRules().FirstOrDefault(r => r.Id == id);
        var same = existing is not null && existing.CategoryId == categoryId && existing.EventId == eventId;
        _recorder.Save(new CategoryRule
        {
            Id = id,
            Pattern = pattern,
            CategoryId = categoryId,
            EventId = eventId,
            UseCount = same ? existing!.UseCount + 1 : 1,
        });
    }
}
