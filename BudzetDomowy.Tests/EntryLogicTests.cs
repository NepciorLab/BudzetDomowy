using BudzetDomowy.Core.Currency;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

public class EntryLogicTests
{
    private static readonly IReadOnlySet<string> Codes = new HashSet<string> { "PLN", "EUR", "USD" };

    private static decimal Eval(string text)
    {
        Assert.True(AmountExpression.TryEvaluate(text, out var value), text);
        return value;
    }

    [Theory]
    [InlineData("2+2*2", 6)]
    [InlineData("(2+2)*2", 8)]
    [InlineData("10/4", 2.5)]
    [InlineData("10/3", 3.33)]
    [InlineData("12,50+7,5", 20)]
    [InlineData("12.5 + 7.5", 20)]
    [InlineData("2×3", 6)]
    [InlineData("9÷3", 3)]
    [InlineData("100-30-20", 50)]
    [InlineData("2*(3+(4-1))", 12)]
    [InlineData("1 234,50+0,50", 1235)]
    [InlineData("-5+10", 5)]
    [InlineData("49,99*3", 149.97)]
    public void Expressions_AreCalculatedWithTheUsualPrecedence_AndRoundedToCents(string text, double expected) =>
        Assert.Equal((decimal)expected, Eval(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2+")]
    [InlineData("*3")]
    [InlineData("2++")]
    [InlineData("(2+3")]
    [InlineData("2+3)")]
    [InlineData("1/0")]
    [InlineData("5-10")]          // a negative amount makes no sense
    [InlineData("1,2,3+1")]
    [InlineData("2 2 x")]
    public void BrokenOrNegativeExpressions_AreRejected(string text) => Assert.False(AmountExpression.TryEvaluate(text, out _));

    [Fact]
    public void ACurrencyMayFollowAnExpression_AsItMayFollowANumber()
    {
        Assert.True(AmountExpression.TryEvaluate("2+2*2 EUR", Codes, out var value, out var currency));
        Assert.Equal((6m, "EUR"), (value, currency));
        Assert.True(AmountExpression.TryEvaluate("12,50 usd", Codes, out value, out currency));
        Assert.Equal((12.5m, "USD"), (value, currency));
        Assert.True(AmountExpression.TryEvaluate("3*3€", Codes, out value, out currency));
        Assert.Equal((9m, "EUR"), (value, currency));
        Assert.True(AmountExpression.TryEvaluate("(5+5)/2", Codes, out value, out currency));
        Assert.Equal((5m, null), (value, currency));
        Assert.False(AmountExpression.TryEvaluate("2+2*2 XYZ", Codes, out _, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void ThePlainNumberAndTheRemainderMarkAreRecognised()
    {
        Assert.True(AmountExpression.IsPlainAmount("12,50", Codes));
        Assert.True(AmountExpression.IsPlainAmount("12,50 EUR", Codes));
        Assert.False(AmountExpression.IsPlainAmount("2+2", Codes));
        Assert.True(AmountExpression.IsRemainder("r"));
        Assert.True(AmountExpression.IsRemainder("  R "));
        Assert.False(AmountExpression.IsRemainder("rr"));
        Assert.False(AmountExpression.IsRemainder("5"));
        Assert.False(AmountExpression.IsRemainder(null));
        Assert.False(AmountExpression.TryEvaluate("r", out _));
    }

    // ----- the place -> category dictionary -----

    private static readonly Guid Transport = Guid.NewGuid();
    private static readonly Guid Food = Guid.NewGuid();
    private static readonly Guid Sweets = Guid.NewGuid();
    private static readonly Guid Salary = Guid.NewGuid();

    private static Transaction Tx(string date, string place, Guid? category, TransactionType type = TransactionType.Expense) => new()
    {
        Id = Guid.NewGuid(), Date = DateOnly.Parse(date), Type = type, Amount = 10, AmountPln = 10, CurrencyCode = "PLN",
        Place = place, CategoryId = category, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Dictionary_SuggestsTheMostFrequentCategoryOfAPlace_ThenTheNextOnes()
    {
        var history = new[]
        {
            Tx("2026-01-01", "MPK", Transport), Tx("2026-01-02", "mpk ", Transport), Tx("2026-01-03", "MPK", Food),
            Tx("2026-01-04", "MPK", Sweets), Tx("2026-01-05", "MPK", Sweets), Tx("2026-01-06", "MPK", Sweets),
            Tx("2026-02-01", "Lewiatan", Food), Tx("2026-02-02", "Lewiatan", Food), Tx("2026-02-03", "Lewiatan", Sweets),
            Tx("2026-03-01", "Firma", Salary, TransactionType.Income),
            Tx("2026-03-02", "", Food), Tx("2026-03-03", "Bez kategorii", null),
        };
        var dictionary = PlaceCategoryDictionary.Build(history, TransactionType.Expense);

        Assert.Equal([Sweets, Transport, Food], dictionary.Suggest("MPK"));         // 3, 2, 1 uses; spelling and case do not matter
        Assert.Equal([Food, Sweets], dictionary.Suggest("  lewiatan"));
        Assert.Empty(dictionary.Suggest("Firma"));                                  // an income place is not in the expense dictionary
        Assert.Empty(dictionary.Suggest(""));
        Assert.Empty(dictionary.Suggest("Nieznane miejsce"));
        Assert.Equal(2, dictionary.Places);

        var income = PlaceCategoryDictionary.Build(history, TransactionType.Income);
        Assert.Equal([Salary], income.Suggest("firma"));
    }

    [Fact]
    public void Dictionary_NextSuggestionSkipsCategoriesThatAreAlreadyUsed()
    {
        var history = new[]
        {
            Tx("2026-01-01", "Sklep", Food), Tx("2026-01-02", "Sklep", Food), Tx("2026-01-03", "Sklep", Food),
            Tx("2026-01-04", "Sklep", Sweets), Tx("2026-01-05", "Sklep", Sweets),
            Tx("2026-01-06", "Sklep", Transport),
        };
        var dictionary = PlaceCategoryDictionary.Build(history, TransactionType.Expense);

        Assert.Equal(Food, dictionary.SuggestNext("Sklep", []));                                // the first line: the most frequent
        Assert.Equal(Sweets, dictionary.SuggestNext("Sklep", [Food]));                          // the first suggestion kept: the second one next
        Assert.Equal(Food, dictionary.SuggestNext("Sklep", [Sweets]));                          // the first line changed: the most frequent is still free
        Assert.Equal(Transport, dictionary.SuggestNext("Sklep", [Food, Sweets]));
        Assert.Null(dictionary.SuggestNext("Sklep", [Food, Sweets, Transport]));                // nothing left to suggest
        Assert.Null(dictionary.SuggestNext("Inny", []));
    }

    [Fact]
    public void Dictionary_RecognisesAPlaceThatContainsOrStartsLikeAKnownOne()
    {
        var history = new[]
        {
            Tx("2026-01-01", "MPK", Transport), Tx("2026-01-02", "Żabka Rynek", Food), Tx("2026-01-03", "Żabka Dworzec", Food), Tx("2026-01-04", "Żabka Dworzec", Sweets),
        };
        var dictionary = PlaceCategoryDictionary.Build(history, TransactionType.Expense);

        Assert.Equal([Transport], dictionary.Suggest("MPK Kraków"));                  // contains a known place
        Assert.Equal([Food, Sweets], dictionary.Suggest("Żabka"));                    // the start of several known places: their totals together
        Assert.Empty(dictionary.Suggest("Ża"));                                       // too short to guess
        Assert.Equal([Transport], dictionary.Suggest("mpk"));
    }

    [Fact]
    public void Dictionary_TiesGoToTheMoreRecentCategory()
    {
        var history = new[] { Tx("2026-01-01", "X", Food), Tx("2026-05-01", "X", Sweets) };
        Assert.Equal([Sweets, Food], PlaceCategoryDictionary.Build(history, TransactionType.Expense).Suggest("x"));
    }
}
