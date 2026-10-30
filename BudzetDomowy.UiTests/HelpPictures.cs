using System.Collections;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using BudzetDomowy.App;
using BudzetDomowy.App.Controls;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Excel;
using BudzetDomowy.Core.Services;
using ClosedXML.Excel;

namespace BudzetDomowy.UiTests;

/// <summary>
/// Draws the pictures of the help (BudzetDomowy.App\Assets\Help) from the real screens, filled with invented, random data
/// (a fixed seed, so the same pictures come out every time). It does nothing unless BUDZET_HELP_PICTURES names the folder to write to:
/// <code>$env:BUDZET_HELP_PICTURES = "G:\...\BudzetDomowy.App\Assets\Help"; dotnet test --filter HelpPictures</code>
/// Nothing is shown on the screen; the views are drawn into bitmaps in memory.
/// </summary>
public class HelpPictures
{
    private const double Scale = 1.5;      // pixels per DIP: the picture stays sharp when the help window shrinks it

    private static readonly string? Folder = Environment.GetEnvironmentVariable("BUDZET_HELP_PICTURES");

    /// <summary>A numbered red frame around what the text of the help talks about.</summary>
    private sealed record Mark(int Number, Func<FrameworkElement, bool> Match, bool First = false);

    private static Mark T(int number, string topic, bool first = false) => new(number, e => Help.GetTopic(e) == topic, first);

    private static Mark N(int number, string name) => new(number, e => e.Name == name, true);

    private static Mark Where(int number, Func<FrameworkElement, bool> match) => new(number, match, true);

    // ----- the invented data -----

    private sealed record Shop(string Place, string Category, string? Sub, double Min, double Max, double PerMonth);

    private static readonly Shop[] Shops =
    [
        new("Sklep Zielony", "Spożywcze", "Supermarket", 25, 160, 9),
        new("Delikatesy Pod Lipą", "Spożywcze", "Supermarket", 12, 90, 6),
        new("Piekarnia Złoty Kłos", "Spożywcze", "Piekarnia", 6, 28, 6),
        new("Targ Warzywny", "Spożywcze", "Owoce i warzywa", 15, 70, 3),
        new("Cukiernia Pod Wiśnią", "Cukiernicze", null, 10, 45, 2),
        new("Drogeria Różana", "Chemia", null, 20, 110, 2),
        new("Dom i Ogród", "Dla domu", "Wyposażenie", 40, 380, 1),
        new("Stacja Horyzont", "Auto", "Paliwo", 160, 320, 3),
        new("Przewozy Miejskie", "Transport", "Komunikacja miejska", 40, 110, 1),
        new("Apteka Zdrowa", "Zdrowie", "Apteka", 15, 120, 3),
        new("Przychodnia Centrum", "Zdrowie", "Lekarze", 120, 300, 0.5),
        new("Księgarnia Bajka", "Dzieci", "Zabawki", 30, 140, 1),
        new("Bistro Kameralne", "Rozrywka", "Restauracje i kawiarnie", 40, 180, 3),
        new("Kino Orzeł", "Rozrywka", "Kino i kultura", 30, 90, 1),
        new("Butik Lawenda", "Ubrania/Buty", null, 80, 420, 0.6),
        new("Szkoła Językowa Alfa", "Edukacja", null, 280, 280, 1),
        new("Elektrownia Północ", "Rachunki", "Prąd", 210, 340, 1),
        new("Gazownia Regionalna", "Rachunki", "Gaz", 90, 210, 1),
        new("Sieć Plus Net", "Rachunki", "Internet i telefon", 69, 69, 1),
        new("Spółdzielnia Słoneczna", "Rachunki", "Czynsz", 1450, 1450, 1),
    ];

    private static Guid Cat(string name, string? sub = null) =>
        sub is null ? DefaultCategories.IdFor(name) : DefaultCategories.IdFor(sub, name);

    private static void FillRandom(TestApp app)
    {
        var random = new Random(2026);
        var today = DateOnly.FromDateTime(DateTime.Today);
        decimal Money(double min, double max) => Math.Round((decimal)(min + random.NextDouble() * (max - min)), 2);

        var holiday = app.Core.Events.Add("Wakacje nad morzem", today.AddDays(-4), today.AddDays(9), "#1E88E5", 4500m);
        var birthday = app.Core.Events.Add("Urodziny Ani", today.AddMonths(-1).AddDays(-3), today.AddMonths(-1).AddDays(-3), "#EC407A", 900m);

        for (var back = 13; back >= 0; back--)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-back);
            var days = back == 0 ? today.Day : DateTime.DaysInMonth(first.Year, first.Month);
            foreach (var shop in Shops)
            {
                var count = (int)Math.Floor(shop.PerMonth) + (random.NextDouble() < shop.PerMonth - Math.Floor(shop.PerMonth) ? 1 : 0);
                for (var i = 0; i < count; i++)
                {
                    var day = shop.PerMonth <= 1 && shop.Min == shop.Max ? Math.Min(days, 5 + Array.IndexOf(Shops, shop) % 10) : random.Next(1, days + 1);
                    app.Core.Transactions.Add(first.AddDays(day - 1), TransactionType.Expense, Money(shop.Min, shop.Max), Cat(shop.Category, shop.Sub), "", null, place: shop.Place);
                }
            }

            var salaryDay = Math.Min(days, 10);
            app.Core.Transactions.Add(first.AddDays(salaryDay - 1), TransactionType.Income, Money(6300, 6600), Cat("Wynagrodzenie"), "wypłata", null, place: "Firma Alfa");
            if (days >= 12) app.Core.Transactions.Add(first.AddDays(11), TransactionType.Income, Money(3000, 3200), Cat("Wynagrodzenie"), "", null, place: "Pracownia Beta");
            if (days >= 20) app.Core.Transactions.Add(first.AddDays(19), TransactionType.Income, 800m, Cat("Świadczenia"), "", null, place: "Urząd");
        }

        // the holiday of this month and the birthday of the last one
        app.Core.Transactions.Add(today.AddDays(-4), TransactionType.Expense, 1850m, Cat("Inne"), "dwa tygodnie", holiday.Id, place: "Pensjonat Fala");
        app.Core.Transactions.Add(today.AddDays(-3), TransactionType.Expense, 268.4m, Cat("Auto", "Paliwo"), "", holiday.Id, place: "Stacja Horyzont");
        app.Core.Transactions.Add(today.AddDays(-2), TransactionType.Expense, 142.5m, Cat("Rozrywka", "Restauracje i kawiarnie"), "ryby", holiday.Id, place: "Bistro Kameralne");
        app.Core.Transactions.Add(today.AddDays(-1), TransactionType.Expense, 96m, Cat("Rozrywka", "Kino i kultura"), "", holiday.Id, place: "Kino Orzeł");
        app.Core.Transactions.Add(today.AddDays(-1), TransactionType.Expense, 64.9m, Cat("Spożywcze", "Supermarket"), "", holiday.Id, place: "Sklep Zielony");
        app.Core.Transactions.Add(birthday.DateFrom, TransactionType.Expense, 185m, Cat("Cukiernicze"), "tort", birthday.Id, place: "Cukiernia Pod Wiśnią");
        app.Core.Transactions.Add(birthday.DateFrom, TransactionType.Expense, 312.6m, Cat("Rozrywka", "Restauracje i kawiarnie"), "", birthday.Id, place: "Bistro Kameralne");
        app.Core.Transactions.Add(today.AddDays(-2), TransactionType.Expense, 48m, Cat("Auto", "Parking i opłaty"), "", holiday.Id, place: "Parking Plaża", currency: "EUR", rateToPln: 4.27m);

        foreach (var (name, limit) in new[] { ("Spożywcze", 1800m), ("Rozrywka", 700m), ("Rachunki", 2400m), ("Auto", 1000m), ("Zdrowie", 350m), ("Chemia", 200m) })
            app.Core.Budgets.SetLimit(Cat(name), limit, today);
        foreach (var (name, limit) in new[] { ("Spożywcze", 20000m), ("Rozrywka", 7000m), ("Auto", 11000m), ("Zdrowie", 3500m) })
            app.Core.Budgets.SetYearlyLimit(Cat(name), limit, today);

        app.Core.Recurring.Add(new RecurringTransaction
        {
            Type = TransactionType.Expense, Amount = 1450m, CategoryId = Cat("Rachunki", "Czynsz"), Name = "Czynsz", Place = "Spółdzielnia Słoneczna", Note = "czynsz",
            Frequency = RecurrenceFrequency.Monthly, Day = 5, StartDate = today.AddMonths(2), ConfirmEach = true,
        });
        app.Core.Recurring.Add(new RecurringTransaction
        {
            Type = TransactionType.Expense, Amount = 69m, CategoryId = Cat("Rachunki", "Internet i telefon"), Name = "Internet", Place = "Sieć Plus Net", Note = "",
            Frequency = RecurrenceFrequency.Monthly, Day = 12, StartDate = today.AddMonths(2),
        });
        app.Core.Recurring.Add(new RecurringTransaction
        {
            Type = TransactionType.Expense, Amount = 120m, CategoryId = Cat("Rozrywka", "Sport i basen"), Name = "Karnet na basen", Place = "Aquapark Fala", Note = "",
            Frequency = RecurrenceFrequency.Weekly, Day = 6, StartDate = today.AddMonths(2),
        });

        var power = app.Core.Payments.AddPayee("Elektrownia Północ", Nrb("109010140000071219812874"), Cat("Rachunki", "Prąd"));
        var water = app.Core.Payments.AddPayee("Wodociągi Miejskie", Nrb("102030405060708090102030"), Cat("Rachunki"));
        var rent = app.Core.Payments.AddPayee("Spółdzielnia Słoneczna", Nrb("116022460000500000012345"), Cat("Rachunki", "Czynsz"));
        app.Core.Payments.AddPayments(power.Id,
        [
            new NewPayment(today.AddDays(2), "Faktura FV/2026/10", 312.40m),
            new NewPayment(today.AddMonths(1).AddDays(2), "Faktura FV/2026/11", 298.15m),
            new NewPayment(today.AddMonths(2).AddDays(2), "Faktura FV/2026/12", 305.00m),
        ]);
        var waterBill = app.Core.Payments.AddPayments(water.Id, [new NewPayment(today.AddDays(9), "Rozliczenie za wodę, III kwartał", 187.60m)]);
        app.Core.Payments.AddPayments(rent.Id, [new NewPayment(today.AddDays(-2), "Czynsz za bieżący miesiąc", 1450m)]);
        app.Core.Payments.MarkScheduled(waterBill[0].Id);
    }

    /// <summary>A Polish account number (26 digits) with the right check digits for the 24 digits given (bank and account).</summary>
    private static string Nrb(string body24)
    {
        var rest = BigInteger.Parse(body24 + "252100") % 97;       // "PL00" moved to the end as 252100
        return (98 - (int)rest).ToString("00") + body24;
    }

    // ----- drawing -----

    private static void Shoot(string name, FrameworkElement content, double width, double height, params Mark[] marks)
    {
        var page = new Grid { Background = (Brush)Application.Current.FindResource("SystemControlPageBackgroundAltHighBrush") };
        System.Windows.Documents.TextElement.SetForeground(page, (Brush)Application.Current.FindResource("SystemControlPageTextBaseHighBrush"));
        var overlay = new Canvas { IsHitTestVisible = false };
        page.Children.Add(content);
        page.Children.Add(overlay);

        UiHost.Layout(page, width, height);
        foreach (var chart in UiHost.Descendants<DonutChart>(page)) chart.CompleteAnimation();
        foreach (var chart in UiHost.Descendants<BarBreakdownChart>(page)) chart.CompleteAnimation();
        UiHost.Layout(page, width, height);

        var red = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        foreach (var mark in marks)
        {
            var found = new[] { content }.Concat(UiHost.Descendants<FrameworkElement>(content))
                .Where(e => UiHost.IsShown(e) && e.ActualWidth > 2 && e.ActualHeight > 2 && mark.Match(e)).ToList();
            if (found.Count == 0) throw new InvalidOperationException($"Picture {name}: nothing to mark as {mark.Number}.");
            if (mark.First) found = [found[0]];
            var box = Rect.Empty;
            foreach (var e in found) box.Union(e.TransformToAncestor(page).TransformBounds(new Rect(e.RenderSize)));
            box.Inflate(2, 2);

            var frame = new Rectangle { Width = box.Width, Height = box.Height, Stroke = red, StrokeThickness = 2, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(frame, box.Left);
            Canvas.SetTop(frame, box.Top);
            overlay.Children.Add(frame);

            var badge = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = red, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
                Child = new TextBlock { Text = mark.Number.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Canvas.SetLeft(badge, Math.Max(0, box.Left - 9));
            Canvas.SetTop(badge, Math.Max(0, box.Top - 9));
            overlay.Children.Add(badge);
        }

        UiHost.Layout(page, width, height);
        var bitmap = new RenderTargetBitmap((int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(page);
        page.Children.Clear();
        Save(name, Trim(bitmap));
    }

    /// <summary>Cuts off the empty part at the bottom (the views are drawn taller than they need to be).</summary>
    private static BitmapSource Trim(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int[] background = [pixels[0], pixels[1], pixels[2]];
        var last = 0;
        for (var y = bitmap.PixelHeight - 1; y >= 0 && last == 0; y--)
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                if (Math.Abs(pixels[i] - background[0]) + Math.Abs(pixels[i + 1] - background[1]) + Math.Abs(pixels[i + 2] - background[2]) > 12) { last = y; break; }
            }

        var height = Math.Min(bitmap.PixelHeight, last + (int)(14 * Scale));
        return height >= bitmap.PixelHeight ? bitmap : new CroppedBitmap(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, Math.Max(height, 8)));
    }

    private static void Save(string name, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(Folder!, name + ".png"));
        encoder.Save(stream);
    }

    /// <summary>The content of a window as a view of its own (the window itself is never shown).</summary>
    private static FrameworkElement Detach(Window window, object dataContext)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        foreach (DictionaryEntry entry in window.Resources)
            if (!content.Resources.Contains(entry.Key)) content.Resources.Add(entry.Key, entry.Value);
        content.DataContext = dataContext;
        return content;
    }

    private static void With(Action<TestApp> pictures)
    {
        if (string.IsNullOrEmpty(Folder)) return;
        Directory.CreateDirectory(Folder);
        using var app = new TestApp(sampleData: false);
        FillRandom(app);
        UiHost.Run(() =>
        {
            ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;     // the pictures are light, whatever Windows uses
            pictures(app);
        });
    }

    // ----- the pictures -----

    [Fact]
    public void MainWindowAndDashboard() => With(app =>
    {
        var main = app.Get<MainViewModel>();
        main.Navigate("dashboard");
        var window = new MainWindow(main, app.Get<SettingsStore>());
        var content = Detach(window, main);
        Shoot("main-window", content, 1180, 800,
            T(1, "main.header"), T(2, "main.quick"), T(3, "main.nav"), Where(4, e => e is ContentControl { Content: DashboardViewModel }),
            new Mark(5, e => Help.GetTopic(e) is "main.sync" or "main.footer"), T(6, "help.button"));

        var vm = app.Get<DashboardViewModel>();
        var view = new DashboardView { DataContext = vm };
        Shoot("dashboard", view, 1180, 1100,
            T(1, "dashboard.month"), T(2, "dashboard.tiles"), N(3, "CategoryRing"),
            Where(4, e => e is ItemsControl ic && ReferenceEquals(ic.ItemsSource, vm.Slices)),
            Where(5, e => e is ScrollViewer { Content: BarBreakdownChart }), T(6, "dashboard.recent"), T(7, "dashboard.budgets"));
    });

    [Fact]
    public void EntryWindows() => With(app =>
    {
        var vm = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: false)
        {
            Place = "Sklep Zielony", TotalText = "184,30", Note = "zakupy na weekend",
        };
        vm.Lines[0].AmountExpression = "120";
        vm.CommitLine(vm.Lines[0]);
        vm.Lines[1].CategoryId = Cat("Chemia");
        var expense = new EntryWindow(vm);
        Shoot("entry-expense", Detach(expense, vm), 780, 560,
            N(1, "DateBox"), N(2, "PlaceBox"), N(3, "TotalBox"), N(4, "CurrencyBox"), N(5, "EventBox"), N(6, "NoteBox"),
            N(7, "LinesList"), N(8, "AddLineButton"), N(9, "ConfirmButton"));

        var income = new EntryViewModel(app.Core, app.Get<ToastService>(), isIncome: true) { TotalText = "6420", Note = "wypłata za październik" };
        income.Lines[0].CategoryId = Cat("Wynagrodzenie");
        Shoot("entry-income", Detach(new IncomeWindow(income), income), 560, 360);

        var calculator = new CalculatorViewModel();
        foreach (var key in "12") calculator.DigitCommand.Execute(key.ToString());
        calculator.OperatorCommand.Execute("+");
        calculator.DigitCommand.Execute("3");
        calculator.OperatorCommand.Execute("*");
        calculator.DigitCommand.Execute("2");
        calculator.EqualCommand.Execute(null);
        Shoot("calculator", Detach(new CalculatorWindow(calculator), calculator), 330, 500);
    });

    [Fact]
    public void TransactionsBudgetsEventsRecurring() => With(app =>
    {
        var transactions = app.Get<TransactionsViewModel>();
        var view = new TransactionsView { DataContext = transactions };
        Shoot("transactions", view, 1000, 560,
            T(1, "transactions.period"), T(2, "transactions.filters"), N(3, "Grid"), T(4, "transactions.sums"), T(5, "transactions.export"));

        var selecting = new TransactionsView { DataContext = transactions };
        UiHost.Layout(selecting, 1000, 560);
        var grid = (DataGrid)selecting.FindName("Grid")!;
        foreach (var row in transactions.Rows.Take(4)) grid.SelectedItems.Add(row);
        Shoot("transactions-selection", selecting, 1000, 560, N(1, "Grid"), T(2, "transactions.sums"));

        var budgets = new BudgetsView { DataContext = app.Get<BudgetsViewModel>() };
        Shoot("budgets", budgets, 1000, 560, T(1, "budgets.copy"), T(2, "budgets.limits"), T(3, "budgets.yearly"));

        var events = app.Get<EventsViewModel>();
        events.Selected = events.Items.First(e => e.Name.StartsWith("Wakacje"));
        Shoot("events", new EventsView { DataContext = events }, 1000, 860, T(1, "events.list"), T(2, "events.edit"), T(3, "events.summary"));

        var recurring = app.Get<RecurringViewModel>();
        recurring.Selected = recurring.Items[0];
        Shoot("recurring", new Border { Padding = new Thickness(16, 12, 16, 8), Child = new RecurringView { DataContext = recurring } }, 1000, 640, T(1, "recurring.list"), T(2, "recurring.edit"), T(3, "recurring.confirm"));
    });

    [Fact]
    public void Payments() => With(app =>
    {
        var payments = app.Get<PaymentsViewModel>();
        Shoot("payments", new PaymentsView { DataContext = payments }, 1000, 460,
            T(1, "payments.plan"), T(2, "payments.payees"), T(3, "payments.list"), T(4, "payments.pay"));

        var entry = new PaymentEntryViewModel(app.Core, app.Get<FakePaymentDialogs>(), app.Get<ToastService>());
        entry.Rows[0].Title = "Faktura FV/2026/13";
        entry.Rows[0].AmountExpression = "312,40";
        entry.CommitRow(entry.Rows[0]);
        Shoot("payment-entry", Detach(new PaymentEntryWindow(entry), entry), 760, 520);

        var bill = app.Core.Reader.GetPlannedPayments().OrderBy(p => p.DueDate).First(p => p.Status == PaymentStatus.Pending && p.DueDate >= DateOnly.FromDateTime(DateTime.Today));
        var pay = new PaymentPayViewModel(app.Core, app.Get<ToastService>(), bill);
        Shoot("pay", Detach(new PayWindow(pay), pay), 780, 470);

        app.Get<PaymentReminderRunner>().Run(DateOnly.FromDateTime(DateTime.Today));
        var reminder = app.Get<FakePaymentReminderPrompt>().Asked.First();
        Shoot("reminder", Detach(new PaymentReminderWindow(reminder), reminder), 560, 360);
    });

    [Fact]
    public void Reports() => With(app =>
    {
        var reports = app.Get<ReportsViewModel>();
        Shoot("reports-overview", new ReportsView { DataContext = reports }, 1000, 1700);

        reports.Tab = ReportTab.Compare;
        Shoot("reports-compare", new ReportsView { DataContext = reports }, 1000, 1000);

        reports.Tab = ReportTab.Places;
        reports.Places.SelectedRow = reports.Places.Rows.First();
        Shoot("reports-places", new ReportsView { DataContext = reports }, 1000, 760);

        reports.Tab = ReportTab.Charts;
        reports.Charts.AddTop(5, TransactionType.Expense);
        Shoot("reports-charts", new ReportsView { DataContext = reports }, 1000, 900);
    });

    [Fact]
    public void Settings() => With(app =>
    {
        var vm = app.Get<SettingsViewModel>();
        foreach (var (tab, name) in new[]
                 {
                     (0, "settings-general"), (1, "settings-appearance"), (2, "settings-categories"), (3, "settings-importexport"),
                     (4, "settings-diagnostics"), (5, "settings-backup"), (SettingsViewModel.ShortcutsTab, "settings-shortcuts"), (SettingsViewModel.PlacesTab, "settings-places"),
                 })
        {
            vm.SelectedTab = tab;
            Shoot(name, new SettingsView { DataContext = vm }, 1000, 700);
        }
    });

    [Fact]
    public void ImportAndWizard()
    {
        if (string.IsNullOrEmpty(Folder)) return;
        Directory.CreateDirectory(Folder);
        using var app = new TestApp(sampleData: false);
        FillRandom(app);
        UiHost.Run(() => ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light);

        var path = Path.Combine(Path.GetTempPath(), "help-import-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var random = new Random(11);
        using (var wb = new XLWorkbook())
        {
            foreach (var (name, month) in new[] { ("Wrz", new DateTime(2026, 9, 1)), ("Paź", new DateTime(2026, 10, 1)) })
            {
                var ws = wb.AddWorksheet(name);
                ws.Cell(2, 2).Value = month;
                ws.Cell(3, 4).Value = "Przychody";
                ws.Cell(3, 5).Value = 6400;
                string[] heads = ["Data", "", "Gdzie", "Kategoria", "Opis", "Kwota"];
                for (var c = 0; c < heads.Length; c++) ws.Cell(6, c + 1).Value = heads[c];
                var row = 7;
                foreach (var shop in Shops.Take(8))
                {
                    ws.Cell(row, 1).Value = row - 6;
                    ws.Cell(row, 2).Value = month.AddDays(random.Next(0, 27));
                    ws.Cell(row, 3).Value = shop.Place;
                    ws.Cell(row, 4).Value = shop.Category;
                    ws.Cell(row, 6).Value = Math.Round(shop.Min + random.NextDouble() * (shop.Max - shop.Min), 2);
                    row++;
                }
            }

            wb.SaveAs(path);
        }

        try
        {
            UiHost.RunAsync(async () =>
            {
                var vm = app.Get<ImportViewModel>();
                var window = new ImportWindow(vm);
                var content = Detach(window, vm);
                vm.FilePath = path;
                await vm.LoadFileAsync();
                await vm.NextCommand.ExecuteAsync(null);
                Shoot("import-2-columns", content, 1000, 640);
            });
        }
        finally { File.Delete(path); }

        UiHost.Run(() =>
        {
            var categories = CategoryHelpers.Flatten(app.Core.Reader.GetCategories(), c => !c.IsArchived, alphabetical: true);
            var bad = new ImportRow("Wrz", 7, new DateOnly(2026, 9, 1), 0, TransactionType.Expense, "Sklep Zielony", "zakupy", "Spożywcze", ImportRowStatus.Error, "bad-amount") { RawAmount = "12,3x" };
            var rowVm = new ImportRowEditViewModel(bad, categories, "błąd: nie można odczytać kwoty");
            Shoot("import-row", Detach(new ImportRowWindow(rowVm), rowVm), 560, 560);

            var wizard = new FirstRunViewModel(new AppSettings()) { FolderPath = @"C:\Users\Ania\OneDrive\BudzetDomowy_dane", DeviceName = "Komputer Ani" };
            Shoot("first-run", Detach(new FirstRunWindow(wizard), wizard), 640, 460);

            var sync = new SyncDetailsViewModel(app.Core);
            Shoot("sync-details", Detach(new SyncDetailsWindow(sync), sync), 700, 520);
        });
    }

    /// <summary>
    /// The help window really shown, but far outside the screen and without taking the focus: the tree and the document need a window that has
    /// been opened (indentation, templates), and the picture is taken from it.
    /// </summary>
    [Fact]
    public void HelpItself() => With(app =>
    {
        var window = new HelpWindow
        {
            Left = -20000, Top = -20000, Width = 1100, Height = 680, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.ShowTopic("entry.total");
            UiHost.Pump();
            UiHost.Layout((FrameworkElement)window.Content, 1100, 680);
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * Scale), (int)(content.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Save("help-window", Trim(bitmap));
        }
        finally { window.Close(); }
    });
}
