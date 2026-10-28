namespace BudzetDomowy.App.ViewModels;

/// <summary>A screen that shows one week, month or year and can step to the previous or next one (Alt + arrows).</summary>
public interface IPeriodNavigable
{
    void PreviousPeriod();
    void NextPeriod();
}

/// <summary>A screen that reads its figures again each time it is opened, so it never shows what was true before the last change on another screen.</summary>
public interface IRefreshOnShow
{
    void RefreshOnShow();
}
