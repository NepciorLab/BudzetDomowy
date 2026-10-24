using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>User-visible texts live in Resources/Strings.resx (Polish). Missing keys show as !Key! so they are easy to spot.</summary>
public static class Res
{
    private static readonly ResourceManager Manager = new("BudzetDomowy.App.Resources.Strings", typeof(Res).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>XAML: Text="{infra:Str NavTransactions}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public StrExtension() { }
    public StrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Res.Get(Key);
}
