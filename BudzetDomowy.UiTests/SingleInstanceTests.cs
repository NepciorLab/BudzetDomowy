using System.Windows;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.UiTests;

/// <summary>A second start of the program asks the running copy to come to the front instead of showing a message.</summary>
public class SingleInstanceTests
{
    private static string NewName() => @"Local\BudzetDomowy-test-" + Guid.NewGuid().ToString("N") + "-show";

    [Fact]
    public void ARequest_ReachesTheListeningCopy_EveryTime()
    {
        var name = NewName();
        using var seen = new SemaphoreSlim(0);
        using var listener = SingleInstance.Listen(name, () => seen.Release());

        Assert.True(SingleInstance.Request(name, TimeSpan.Zero));
        Assert.True(seen.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(SingleInstance.Request(name, TimeSpan.Zero));                            // not only the first time
        Assert.True(seen.Wait(TimeSpan.FromSeconds(3)));
        Assert.False(seen.Wait(TimeSpan.FromMilliseconds(300)));                             // one request, one call
    }

    [Fact]
    public void WithNobodyListening_TheRequestSaysSo_AfterWaitingAMoment()
    {
        var started = DateTime.UtcNow;
        Assert.False(SingleInstance.Request(NewName(), TimeSpan.FromMilliseconds(350)));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300));            // it waits for a copy that is still starting
        Assert.False(SingleInstance.Request(NewName(), TimeSpan.Zero));
    }

    [Fact]
    public void ACopyThatStartsListeningWhileTheRequestWaits_IsStillReached()
    {
        var name = NewName();
        using var seen = new SemaphoreSlim(0);
        IDisposable? listener = null;
        var starter = Task.Run(async () =>
        {
            await Task.Delay(250);
            listener = SingleInstance.Listen(name, () => seen.Release());
        });

        Assert.True(SingleInstance.Request(name, TimeSpan.FromSeconds(3)));
        starter.Wait();
        Assert.True(seen.Wait(TimeSpan.FromSeconds(3)));
        listener!.Dispose();
    }

    [Fact]
    public void AClosedListener_StopsAnswering()
    {
        var name = NewName();
        using var seen = new SemaphoreSlim(0);
        var listener = SingleInstance.Listen(name, () => seen.Release());
        listener.Dispose();

        Assert.False(SingleInstance.Request(name, TimeSpan.Zero));
        Assert.False(seen.Wait(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void TheProfileIsPartOfTheName_SoACopyWithAnotherProfileIsNotDisturbed()
    {
        Assert.NotEqual(SingleInstance.EventName(null), SingleInstance.EventName("B"));
        Assert.Equal(SingleInstance.EventName(null), SingleInstance.EventName(null));
    }

    // ----- the window -----

    private static Window Shown(string title, WindowState state = WindowState.Normal, Window? owner = null)
    {
        var window = new Window { Title = title, Width = 300, Height = 200, ShowActivated = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        if (owner is not null) window.Owner = owner;
        window.Show();
        window.WindowState = state;
        return window;
    }

    [Fact]
    public void AMinimisedMainWindow_IsMaximisedAndBroughtToTheFront()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var main = Shown("główne", WindowState.Minimized);
            try
            {
                Assert.Equal(WindowState.Minimized, main.WindowState);

                SingleInstance.BringToFront(main, [main]);

                Assert.Equal(WindowState.Maximized, main.WindowState);
                Assert.True(main.IsVisible);
                Assert.False(main.Topmost);                                                  // it is not left above everything else
            }
            finally { main.Close(); }
        });
    }

    [Fact]
    public void AHiddenMainWindow_IsShownAgain()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var main = Shown("główne");
            try
            {
                main.Hide();
                Assert.False(main.IsVisible);

                SingleInstance.BringToFront(main, [main]);

                Assert.True(main.IsVisible);
                Assert.Equal(WindowState.Maximized, main.WindowState);
            }
            finally { main.Close(); }
        });
    }

    [Fact]
    public void WhenADialogIsOpen_TheDialogComesForward_AndTheMainWindowIsMaximisedBehindIt()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            var main = Shown("główne", WindowState.Minimized);
            var dialog = Shown("okno wpisu", owner: main);
            try
            {
                SingleInstance.BringToFront(main, [main, dialog]);

                Assert.Equal(WindowState.Maximized, main.WindowState);
                Assert.True(dialog.IsVisible);
                Assert.Equal(WindowState.Normal, dialog.WindowState);                        // only the main window is maximised
            }
            finally
            {
                dialog.Close();
                main.Close();
            }
        });
    }

    [Fact]
    public void WithoutAMainWindowYet_AnyVisibleWindowIsBroughtForward_AndNothingBreaksWhenThereIsNone()
    {
        using var app = new TestApp(sampleData: false);
        UiHost.Run(() =>
        {
            SingleInstance.BringToFront(null, []);                                           // nothing open: nothing happens

            var wizard = Shown("pierwsze uruchomienie", WindowState.Minimized);
            try
            {
                SingleInstance.BringToFront(null, [wizard]);
                Assert.Equal(WindowState.Normal, wizard.WindowState);                        // restored, not maximised: it is not the main window
            }
            finally { wizard.Close(); }
        });
    }
}
