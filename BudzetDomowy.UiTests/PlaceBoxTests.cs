using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BudzetDomowy.App.Controls;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.UiTests;

/// <summary>The place box: the most popular matching place is written after the caret, Enter takes it, Delete drops it.</summary>
public class PlaceBoxTests
{
    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static readonly PlaceSuggestion[] Places =
    [
        new("Lewiatan", 9, true, false), new("Lidl", 5, true, false), new("Leroy Merlin", 2, true, false), new("Biedronka", 1, true, false),
    ];

    private static void WithBox(Action<PlaceBox> test) => UiHost.Run(() =>
    {
        var box = new PlaceBox { Suggestions = Places };
        box.ApplyTemplate();
        test(box);
    });

    /// <summary>What the user types: the preview event marks it as typing, then the letter is added at the end (the typed selection is replaced).</summary>
    private static void Type(PlaceBox box, string letter)
    {
        box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, letter))
        {
            RoutedEvent = UIElement.PreviewTextInputEvent,
        });
        box.Text = box.Text[..box.SelectionStart] + letter;
    }

    private static KeyEventArgs Press(PlaceBox box, Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
        box.RaiseEvent(e);
        if (!e.Handled && key == Key.Delete && box.SelectionLength > 0) box.SelectedText = "";         // what the text box does with Delete
        return e;
    }

    private static string Shown(PlaceBox box) => box.Text;
    private static string Selected(PlaceBox box) => box.SelectedText;

    [Fact]
    public void TypingALetter_ShowsTheMostPopularMatchAfterTheCaret_AndEnterTakesItWithItsCapitals() => WithBox(box =>
    {
        Type(box, "l");
        Assert.Equal("lewiatan", Shown(box));                      // the typed letter keeps its case until the place is taken
        Assert.Equal("ewiatan", Selected(box));
        Assert.True(box.IsSuggesting);

        Press(box, Key.Enter);
        Assert.Equal("Lewiatan", box.Text);
        Assert.Equal("", box.SelectedText);
        Assert.False(box.IsSuggesting);
    });

    [Fact]
    public void TypingMoreLetters_ReplacesTheCompletion_WithTheNextMatch() => WithBox(box =>
    {
        Type(box, "l");
        Type(box, "i");
        Assert.Equal("lidl", Shown(box));
        Assert.Equal("dl", Selected(box));

        Press(box, Key.Tab);
        Assert.Equal("Lidl", box.Text);
    });

    [Fact]
    public void Delete_DropsOnlyTheCompletion_SoANewNameCanBeTyped() => WithBox(box =>
    {
        Type(box, "l");
        Type(box, "i");
        Press(box, Key.Delete);
        Assert.Equal("li", box.Text);
        Assert.False(box.IsSuggesting && box.SelectionLength > 0);

        Press(box, Key.Enter);                                     // nothing to take: the typed text stays
        Assert.Equal("li", box.Text);
    });

    [Fact]
    public void ArrowKeys_WalkThroughTheAlphabeticalList_AndTheBoxFollows() => WithBox(box =>
    {
        Type(box, "l");                                            // the list: Leroy Merlin, Lewiatan, Lidl; Lewiatan is the popular one
        Assert.Equal("ewiatan", Selected(box));

        Assert.True(Press(box, Key.Down).Handled);
        Assert.Equal("lidl", Shown(box));                          // after Lewiatan comes Lidl
        Press(box, Key.Up);
        Press(box, Key.Up);
        Assert.Equal("leroy Merlin", Shown(box));                  // the typed letter stays as typed until Enter
        Press(box, Key.Enter);
        Assert.Equal("Leroy Merlin", box.Text);
    });

    [Fact]
    public void Escape_DropsTheCompletion_AndRightArrowTakesIt() => WithBox(box =>
    {
        Type(box, "b");
        Assert.Equal("biedronka", Shown(box));
        Assert.True(Press(box, Key.Escape).Handled);
        Assert.Equal("b", box.Text);
        Assert.False(box.IsSuggesting);

        Type(box, "i");
        Press(box, Key.Right);
        Assert.Equal("Biedronka", box.Text);
    });

    [Fact]
    public void ATextSetFromOutside_OrNothingMatching_ShowsNoCompletion() => WithBox(box =>
    {
        box.Text = "Lew";                                         // a binding, not the user
        Assert.Equal("Lew", box.Text);
        Assert.False(box.IsSuggesting);

        box.Text = "";
        Type(box, "x");
        Assert.Equal("x", box.Text);
        Assert.False(box.IsSuggesting);
    });
}
