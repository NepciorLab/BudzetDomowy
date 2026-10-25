using BudzetDomowy.Core.Services;

namespace BudzetDomowy.Tests;

public class CalculatorTests
{
    /// <summary>Presses keys given as text: digits, a comma, + - * /, = , C (clear), B (backspace), N (change sign).</summary>
    private static Calculator Press(string keys)
    {
        var c = new Calculator();
        foreach (var k in keys)
        {
            if (char.IsDigit(k)) c.Digit(k - '0');
            else if (k == ',') c.Comma();
            else if (k == '=') c.Equals();
            else if (k == 'C') c.Clear();
            else if (k == 'B') c.Backspace();
            else if (k == 'N') c.Negate();
            else Assert.True(Calculator.TryParseOp(k, out var op) && Apply(c, op), $"unknown key {k}");
        }

        return c;
    }

    private static bool Apply(Calculator c, CalcOp op)
    {
        c.Operator(op);
        return true;
    }

    [Theory]
    [InlineData("12+3=", "15")]
    [InlineData("12-30=", "-18")]
    [InlineData("6*7=", "42")]
    [InlineData("9/4=", "2,25")]
    [InlineData("0,1+0,2=", "0,3")]                       // decimal, not binary floating point
    [InlineData("1/3=", "0,3333333333")]
    [InlineData("100,5*2=", "201")]
    public void Results(string keys, string expected)
    {
        var c = Press(keys);
        Assert.Equal(expected, c.Display);
        Assert.Equal(expected, c.ResultText);
    }

    [Fact]
    public void Operations_AreExecutedOneAtATime_LikeADeskCalculator()
    {
        var c = Press("12+3*2=");
        Assert.Equal("30", c.Display);                      // (12 + 3) × 2, no operator precedence
    }

    [Fact]
    public void TheUpperLineShowsWhatIsWaitingAndWhatWasCalculated()
    {
        var c = Press("12+");
        Assert.Equal("12 +", c.Expression);
        Assert.Equal("12", c.Display);                       // until the next number is typed
        c.Digit(3);
        Assert.Equal("3", c.Display);
        c.Equals();
        Assert.Equal("12 + 3 =", c.Expression);
        Assert.Equal("15", c.Display);

        Assert.Equal("20 ÷", Press("20/").Expression);
        Assert.Equal("20 −", Press("20-").Expression);
    }

    [Fact]
    public void AnOperatorPressedAgainReplacesThePrevious()
    {
        Assert.Equal("84", Press("12+-*7=").Display);        // 12 × 7: the last operator wins
    }

    [Fact]
    public void TypingANumber_Rules()
    {
        Assert.Equal("0", Press("").Display);
        Assert.Equal("7", Press("007").Display);            // no leading zeros
        Assert.Equal("0,5", Press(",5").Display);           // a comma first means 0,
        Assert.Equal("1,25", Press("1,2,5").Display);       // a second comma is ignored
        Assert.Equal("0,", Press("0,").Display);
        Assert.Equal("123456789012345", Press("1234567890123456789").Display);   // at most 15 digits
        Assert.Equal("1,5", Press("1,5").ResultText);
        Assert.Equal("12", Press("12,").ResultText);        // a hanging comma is not copied
    }

    [Fact]
    public void ADigitAfterAResult_StartsANewCalculation()
    {
        var c = Press("2+2=");
        Assert.Equal("4", c.Display);
        c.Digit(9);
        Assert.Equal("9", c.Display);
        Assert.Equal("", c.Expression);
        c.Operator(CalcOp.Add);                             // but an operator carries the result on
        Assert.Equal("9 +", c.Expression);
        Assert.Equal("14", Press("2+2=+10=").Display);
    }

    [Fact]
    public void ChangeSignBackspaceAndClear()
    {
        Assert.Equal("-5", Press("5N").Display);
        Assert.Equal("5", Press("5NN").Display);
        Assert.Equal("0", Press("N").Display);
        Assert.Equal("3", Press("N3").Display);             // nothing to negate yet
        Assert.Equal("-8", Press("10N+2=").Display);        // -10 + 2

        Assert.Equal("12", Press("123B").Display);
        Assert.Equal("0", Press("5B").Display);
        Assert.Equal("0", Press("5NB").Display);
        Assert.Equal("1", Press("1,B").Display);
        Assert.Equal("4", Press("2+2=B").Display);          // a result is not edited digit by digit

        var c = Press("12+3");
        c.Clear();
        Assert.Equal(("0", ""), (c.Display, c.Expression));
        Assert.Equal("5", Press("12+3C5").Display);
    }

    [Fact]
    public void DividingByZero_IsAnErrorUntilCleared()
    {
        var c = Press("5/0=");
        Assert.True(c.HasError);
        Assert.Equal(CalcError.DivideByZero, c.Error);
        Assert.Equal("", c.ResultText);                     // nothing to copy
        c.Operator(CalcOp.Add);                             // ignored
        c.Equals();
        Assert.True(c.HasError);

        c.Digit(4);                                         // typing starts afresh
        Assert.Equal(("4", false), (c.Display, c.HasError));

        var again = Press("5/0=");
        again.Backspace();
        Assert.False(again.HasError);
        Assert.Equal("0", again.Display);
    }

    [Fact]
    public void ATooBigNumber_IsAnError()
    {
        var c = new Calculator();
        for (var i = 0; i < 15; i++) c.Digit(9);
        c.Operator(CalcOp.Multiply);
        for (var i = 0; i < 15; i++) c.Digit(9);
        c.Equals();                                         // about 10^30: more than a decimal can hold
        Assert.Equal(CalcError.Overflow, c.Error);
    }

    [Fact]
    public void ResultsAreRoundedToTenPlaces_WithoutTrailingZeros()
    {
        Assert.Equal("0,6666666667", Press("2/3=").Display);
        Assert.Equal("2,5", Press("5/2=").ResultText);
        Assert.Equal("1000000", Press("1000*1000=").ResultText);        // no grouping in the text to paste
    }

    [Theory]
    [InlineData('+', true)]
    [InlineData('-', true)]
    [InlineData('−', true)]
    [InlineData('*', true)]
    [InlineData('×', true)]
    [InlineData('/', true)]
    [InlineData('÷', true)]
    [InlineData('a', false)]
    [InlineData('=', false)]
    public void OperatorCharacters(char c, bool expected) => Assert.Equal(expected, Calculator.TryParseOp(c, out _));
}
