using System.Runtime.InteropServices;
using System.Windows;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Puts text on the clipboard; another program holding the clipboard at that moment is not an error worth showing.</summary>
public static class ClipboardText
{
    public static void Copy(string text)
    {
        try { Clipboard.SetDataObject(text, true); }
        catch (ExternalException) { /* the clipboard is busy: nothing is lost, copy again */ }
    }
}
