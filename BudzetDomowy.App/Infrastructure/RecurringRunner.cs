using System.Globalization;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.App.Infrastructure;

public enum RecurringAnswer { Yes, No, Later }

/// <summary>What the question "did this happen?" shows.</summary>
public sealed record RecurringQuestion(string Title, string Date, string Amount, string Category, string Schedule);

public interface IRecurringPrompt
{
    /// <summary>False while there is no visible program window to ask in (the question waits).</summary>
    bool CanAsk { get; }

    RecurringAnswer Ask(RecurringQuestion question);
}

public sealed record RecurringOutcome(int Created, int Declined, int Postponed, int WaitingForRate);

/// <summary>
/// Books due recurring entries and asks about those marked "confirm each time": yes books the entry, no drops that
/// occurrence for good, later keeps the question for the next start of the program (or the "generate now" button).
/// </summary>
public sealed class RecurringRunner
{
    private readonly BudgetCore _core;
    private readonly IRecurringPrompt _prompt;
    private readonly HashSet<(Guid, DateOnly)> _postponed = [];
    private bool _busy;

    public RecurringRunner(BudgetCore core, IRecurringPrompt prompt)
    {
        _core = core;
        _prompt = prompt;
    }

    /// <param name="askAgain">The user asked for it explicitly: ask also about questions postponed earlier.</param>
    public async Task<RecurringOutcome> RunAsync(DateOnly today, bool askAgain = false, CancellationToken ct = default)
    {
        if (_busy) return new RecurringOutcome(0, 0, 0, 0);
        _busy = true;
        try
        {
            var run = await _core.Recurring.GenerateDueAsync(today, ct);
            var created = run.Created;
            var declined = 0;
            var postponed = 0;
            var waiting = run.TemplatesWaitingForRate.Count;
            if (askAgain) _postponed.Clear();

            var stop = false;
            foreach (var pending in run.Pending)
            {
                if (stop || _postponed.Contains((pending.Template.Id, pending.Date)) || !_prompt.CanAsk)
                {
                    postponed++;
                    continue;
                }

                switch (_prompt.Ask(Describe(pending)))
                {
                    case RecurringAnswer.Yes:
                        if (await _core.Recurring.ConfirmAsync(pending.Template.Id, pending.Date, ct)) created++; else waiting++;
                        break;
                    case RecurringAnswer.No:
                        _core.Recurring.Decline(pending.Template.Id, pending.Date);
                        declined++;
                        break;
                    default:
                        _postponed.Add((pending.Template.Id, pending.Date));
                        postponed++;
                        stop = true;     // "later" ends the questions of this run
                        break;
                }
            }

            // answers move the watermark of the templates forward
            if (created + declined > 0) await _core.Recurring.GenerateDueAsync(today, ct);
            return new RecurringOutcome(created, declined, postponed, waiting);
        }
        finally
        {
            _busy = false;
        }
    }

    private RecurringQuestion Describe(PendingOccurrence p)
    {
        var t = p.Template;
        var culture = CultureInfo.CurrentCulture;
        var sign = UiPreferences.Instance.SignOf(t.Type == TransactionType.Income);
        var amount = sign + t.Amount.ToString("N2", culture) + " " + t.CurrencyCode;
        var title = t.Place.Length > 0 ? t.Place : t.Note.Length > 0 ? t.Note : Res.Get("RecurPromptNoName");
        if (t.Place.Length > 0 && t.Note.Length > 0) title += " – " + t.Note;

        var byId = _core.Reader.GetCategories().ToDictionary(c => c.Id);
        var category = CategoryHelpers.PathOf(t.CategoryId, byId);
        var schedule = t.Frequency switch
        {
            RecurrenceFrequency.Weekly => Res.Get("RecurWeekly"),
            RecurrenceFrequency.Yearly => Res.Get("RecurYearly"),
            _ => Res.Get("RecurMonthly"),
        };
        return new RecurringQuestion(title, p.Date.ToString("D", culture), amount, category, schedule);
    }
}
