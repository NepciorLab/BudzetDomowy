using BudzetDomowy.App.Infrastructure.Updates;
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

/// <summary>Stands in for the tray icon and global shortcut: no window, no system hook.</summary>
public sealed class FakeHost : IBackgroundHost
{
    public bool IsActive { get; private set; }
    public bool Refuse { get; set; }
    public event Action? QuickAddRequested;
    public event Action? OpenMainRequested;
    public event Action? ExitRequested;

    public bool Start(out string? error)
    {
        error = Refuse ? "taken" : null;
        if (!Refuse) IsActive = true;
        return !Refuse;
    }

    public void Stop() => IsActive = false;
    public void Dispose() => Stop();
    public void RaiseQuickAdd() => QuickAddRequested?.Invoke();
    public void RaiseOpenMain() => OpenMainRequested?.Invoke();
    public void RaiseExit() => ExitRequested?.Invoke();
}

/// <summary>File pickers that never open a window: they return the paths a test sets.</summary>
public sealed class FakeDialogs : IFileDialogs
{
    public string? NextOpen { get; set; }
    public string? NextSave { get; set; }
    public string? OpenFile(string title, string filter) => NextOpen;
    public string? SaveFile(string title, string filter, string defaultFileName) => NextSave;
    public string? NextFolder { get; set; }
    public string? PickFolder(string title, string? initialFolder) => NextFolder;
}

/// <summary>Answers the "did this happen?" questions from a queue instead of opening a window.</summary>
public sealed class FakeRecurringPrompt : IRecurringPrompt
{
    public bool CanAsk { get; set; } = true;
    public Queue<RecurringAnswer> Answers { get; } = new();
    public List<RecurringQuestion> Asked { get; } = [];

    public RecurringAnswer Ask(RecurringQuestion question)
    {
        Asked.Add(question);
        return Answers.Count > 0 ? Answers.Dequeue() : RecurringAnswer.Later;
    }
}

/// <summary>Answers GitHub requests from a table of canned responses; nothing goes to the network.</summary>
public sealed class FakeGitHub : System.Net.Http.HttpMessageHandler
{
    public Dictionary<string, (System.Net.HttpStatusCode Status, byte[] Body)> Responses { get; } = [];
    public List<System.Net.Http.HttpRequestMessage> Requests { get; } = [];

    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var (status, body) = Responses.GetValueOrDefault(request.RequestUri!.ToString(), (System.Net.HttpStatusCode.NotFound, []));
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(status) { Content = new System.Net.Http.ByteArrayContent(body) });
    }
}

public sealed class FakeUpdatePrompt : IUpdatePrompt
{
    public bool Answer { get; set; }
    public int Asked { get; private set; }
    public bool LastDataIsNewer { get; private set; }

    public bool Confirm(ReleaseInfo release, Version current, bool dataIsNewer = false)
    {
        Asked++;
        LastDataIsNewer = dataIsNewer;
        return Answer;
    }
}

/// <summary>Answers yes/no questions from a setting instead of opening a message box.</summary>
public sealed class FakeMessageDialogs : IMessageDialogs
{
    public bool Answer { get; set; } = true;
    public List<string> Asked { get; } = [];

    public bool Confirm(string title, string text)
    {
        Asked.Add(text);
        return Answer;
    }
}

/// <summary>Counts restarts instead of starting a new process.</summary>
public sealed class FakeRestarter : IAppRestarter
{
    public int Restarts { get; private set; }
    public void Restart() => Restarts++;
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
            .AddSingleton<DiagnosticsViewModel>()
            .AddSingleton<IBackgroundHost, FakeHost>()
            .AddSingleton<IFileDialogs, FakeDialogs>()
            .AddSingleton<IAppRestarter, FakeRestarter>()
            .AddSingleton<FakeMessageDialogs>()
            .AddSingleton<IMessageDialogs>(sp => sp.GetRequiredService<FakeMessageDialogs>())
            .AddSingleton<BackupViewModel>()
            .AddSingleton<FakeGitHub>()
            .AddSingleton<FakeUpdatePrompt>()
            .AddSingleton<IUpdatePrompt>(sp => sp.GetRequiredService<FakeUpdatePrompt>())
            .AddSingleton(sp => new GitHubReleases(new System.Net.Http.HttpClient(sp.GetRequiredService<FakeGitHub>())))
            .AddSingleton(sp => new UpdateService(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<GitHubReleases>(),
                sp.GetRequiredService<IUpdatePrompt>(), sp.GetRequiredService<IAppRestarter>(), Path.Combine(_root, "BudzetDomowy.exe"), new Version(0, 1, 0)))
            .AddSingleton<FakeRecurringPrompt>()
            .AddSingleton<IRecurringPrompt>(sp => sp.GetRequiredService<FakeRecurringPrompt>())
            .AddSingleton<RecurringRunner>()
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
            Type = TransactionType.Expense, Amount = 1200m, CategoryId = Id("Rachunki"), Name = "Czynsz", Place = "Spółdzielnia", Note = "czynsz",
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
