using System.Runtime.CompilerServices;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.UiTests;

/// <summary>
/// Tests must never touch the real settings, log or database of the person running them:
/// everything the application writes by itself goes to a throw-away profile.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void UseThrowAwayProfile() => AppPaths.Initialize(["--profile", "UiTests"]);
}
