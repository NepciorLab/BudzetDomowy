namespace BudzetDomowy.App.ViewModels;

/// <summary>A screen that shows one week, month or year and can step to the previous or next one (Alt + arrows).</summary>
public interface IPeriodNavigable
{
    void PreviousPeriod();
    void NextPeriod();
}
