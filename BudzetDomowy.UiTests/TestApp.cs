using BudzetDomowy.App;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Currency;
using BudzetDomowy.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BudzetDomowy.UiTests;

/// <summary>An offline rate source so UI tests never touch the network.</summary>
public sealed class NoRates : IExchangeRateSource
{
    public Task<IReadOnlyList<ExchangeRate>> GetRatesAsync(string currencyCode, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExchangeRate>>(
            [new ExchangeRate { CurrencyCode = currencyCode, Date = to, Rate = 4.25m, Source = ExchangeRateSource.Nbp, FetchedAt = DateTimeOffset.UtcNow }]);
}

/// <summary>File pickers that never open a window: they return the paths a test sets.</summary>
public sealed class FakeDialogs : IFileDialogs
{
    public string? NextOpen { get; set; }
    public string? NextSave { get; set; }
    public string? OpenFile(string title, string filter) => NextOpen;
    public string? SaveFile(string title, string filter, string defaultFileName) => NextSave;
}

/// <summary>A complete application object graph on temporary folders, with some sample data.</summary>
public sealed class TestApp : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "budzet-ui-" + Guid.NewGuid().ToString("N"));

    public BudgetCore Core { get; }
    public ServiceProvider Services { get; }

    public TestApp(bool sampleData = true)
    {
        var shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared);
        Core = BudgetCore.Create(Path.Combine(_root, "local"), shared, "TEST-PC", rateSource: new NoRates());
        Core.Categories.SeedIfEmpty();
        if (sampleData) AddSampleData();

        Services = new ServiceCollection()
            .AddSingleton(Core)
            .AddSingleton(new SettingsStore(new AppSettings()))
            .AddSingleton<DataHub>()
            .AddSingleton<ToastService>()
            .AddSingleton<QuickAddViewModel>()
            .AddSingleton<TransactionsViewModel>()
            .AddSingleton<CategoriesViewModel>()
            .AddSingleton<EventsViewModel>()
            .AddSingleton<DashboardViewModel>()
            .AddSingleton<BudgetsViewModel>()
            .AddSingleton<ReportsViewModel>()
            .AddSingleton<RecurringViewModel>()
            .AddSingleton<IFileDialogs, FakeDialogs>()
            .AddSingleton(new ImportMappingStore(Path.Combine(_root, "import-mapping.json")))
            .AddSingleton<ExportActions>()
            .AddTransient<ImportViewModel>()
            .AddSingleton<Func<ImportViewModel>>(sp => () => sp.GetRequiredService<ImportViewModel>())
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<MainViewModel>()
            .BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    private void AddSampleData()
    {
        var cats = Core.Reader.GetCategories();
        Guid Id(string name) => cats.First(c => c.Name == name).Id;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var holiday = Core.Events.Add("Wakacje nad morzem", today.AddDays(-2), today.AddDays(8), "#1E88E5", 3000m);

        Core.Transactions.Add(today, TransactionType.Expense, 54.30m, Id("Spożywcze"), "", null, place: "Lewiatan");
        Core.Transactions.Add(today.AddDays(-1), TransactionType.Expense, 12.80m, Id("Cukiernicze"), "dla dzieci", null, place: "Cukiernia");
        Core.Transactions.Add(today.AddDays(-1), TransactionType.Expense, 220m, Id("Rozrywka"), "", holiday.Id, place: "Aquapark");
        Core.Transactions.Add(today, TransactionType.Expense, 48m, Id("Auto"), "", holiday.Id, place: "Orlen", currency: "EUR", rateToPln: 4.25m);
        Core.Transactions.Add(today.AddDays(-3), TransactionType.Expense, 300m, Id("Rachunki"), "prąd", null, place: "Tauron");
        Core.Transactions.Add(today.AddDays(-5), TransactionType.Income, 6500m, Id("Wynagrodzenie"), "", null, place: "Firma");
        Core.Budgets.SetLimit(Id("Spożywcze"), 800m, today);
        Core.Budgets.SetLimit(Id("Rachunki"), 350m, today);
        Core.Budgets.SetLimit(Id("Rozrywka"), 200m, today);
        Core.Transactions.Add(today.AddMonths(-1), TransactionType.Expense, 640m, Id("Spożywcze"), "", null, place: "Lewiatan");
        Core.Transactions.Add(today.AddMonths(-1), TransactionType.Income, 6500m, Id("Wynagrodzenie"), "", null, place: "Firma");
        Core.Transactions.Add(today.AddMonths(-2), TransactionType.Expense, 900m, Id("Auto"), "", null, place: "Warsztat");
        Core.Recurring.Add(new RecurringTransaction
        {
            Type = TransactionType.Expense, Amount = 1200m, CategoryId = Id("Rachunki"), Place = "Spółdzielnia", Note = "czynsz",
            Frequency = RecurrenceFrequency.Monthly, Day = 5, StartDate = today.AddMonths(2),
        });
    }

    public void Dispose()
    {
        UiHost.Run(() => Services.Dispose()); // stops the data hub timer on its own thread before the folder disappears
        Core.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
