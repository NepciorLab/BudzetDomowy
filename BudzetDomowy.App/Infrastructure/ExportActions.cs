using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Asks where to save and writes exports; shared by the transaction list, the reports and the settings.</summary>
public sealed class ExportActions
{
    private readonly BudgetCore _core;
    private readonly IFileDialogs _dialogs;
    private readonly ToastService _toast;

    public ExportActions(BudgetCore core, IFileDialogs dialogs, ToastService toast)
    {
        _core = core;
        _dialogs = dialogs;
        _toast = toast;
    }

    public void Transactions(IReadOnlyList<Transaction> rows, bool csv)
    {
        var stamp = DateTime.Today.ToString("yyyy-MM-dd");
        var path = csv
            ? _dialogs.SaveFile(Res.Get("ExportCsvTitle"), Res.Get("ExportCsvFilter"), $"Transakcje {stamp}.csv")
            : _dialogs.SaveFile(Res.Get("ExportXlsxTitle"), Res.Get("ExportXlsxFilter"), $"Transakcje {stamp}.xlsx");
        if (path is null) return;

        Save(path, () =>
        {
            var categories = _core.Reader.GetCategories();
            var events = _core.Reader.GetSpecialEvents();
            if (csv) ExportService.TransactionsToCsv(path, rows, categories, events);
            else ExportService.TransactionsToXlsx(path, rows, categories, events);
        }, rows.Count);
    }

    public void Report(ReportScope scope, DateOnly anchor)
    {
        var name = scope == ReportScope.Year ? anchor.Year.ToString() : anchor.ToString("yyyy-MM");
        var path = _dialogs.SaveFile(Res.Get("ExportXlsxTitle"), Res.Get("ExportXlsxFilter"), $"Raport {name}.xlsx");
        if (path is null) return;

        Save(path, () => ExportService.ReportToXlsx(path, scope, anchor, _core.Reader.GetTransactions(), _core.Reader.GetCategories()), null);
    }

    private void Save(string path, Action write, int? count)
    {
        try
        {
            write();
            _toast.Show(count is { } n ? Res.Format("ExportDoneCount", n, Path.GetFileName(path)) : Res.Format("ExportDone", Path.GetFileName(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _toast.Show(Res.Format("ExportFailed", e.Message));
        }
    }
}
