using DesktopShift.App.ViewModels;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Presentation.Tests.ViewModels;

[TestClass]
public sealed class ObservationActivityPresentationTests
{
    [TestMethod]
    public void Create_MatchedBrowserWindowNamesRuleAndManagedDesktop()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            new WindowSafeIdentity(
                "msedge.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Chrome_WidgetWin_1"),
            ruleId: "browsers",
            targetDesktopKey: "web");

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("Matched", presentation.Outcome);
        Assert.AreEqual("Matched browsers to web", presentation.Decision);
        Assert.AreEqual("msedge.exe", presentation.ProcessName);
        Assert.Contains(
            "Window class: Chrome_WidgetWin_1",
            presentation.IdentityDetails);
    }

    [TestMethod]
    public void Create_BrowserHelperSurfaceExplainsWhyItWasNotAnIndependentWindow()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Skipped,
            WindowSkipReason.BrowserHelperWindow,
            new WindowSafeIdentity(
                "chrome.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Chrome_MessageWindow"));

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("Skipped", presentation.Outcome);
        Assert.AreEqual("Skipped: Browser Helper Window", presentation.Decision);
        Assert.AreEqual("No matching rule", presentation.Rule);
        Assert.AreEqual("No desktop assigned", presentation.TargetDesktop);
        Assert.Contains(
            "Window class: Chrome_MessageWindow",
            presentation.IdentityDetails);
    }

    [TestMethod]
    public void Create_SkippedSurfaceReportsPrivacySafeIdentityOnly()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Skipped,
            WindowSkipReason.BrowserHelperWindow,
            new WindowSafeIdentity(
                "msedge.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Chrome_RenderWidgetHostHWND"));

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        // WindowSafeIdentity is the only identity the presentation can reach, so
        // window titles, URLs, and command lines cannot leak into the UI.
        Assert.Contains("msedge.exe", presentation.AutomationName);
        Assert.Contains("Browser Helper Window", presentation.AutomationName);
        Assert.DoesNotContain("http", presentation.AutomationName);
        Assert.DoesNotContain("--profile-directory", presentation.AutomationName);
        Assert.AreEqual(string.Empty, presentation.Diagnostic);
    }

    private static WindowObservationActivity CreateActivity(
        WindowObservationOutcome outcome,
        WindowSkipReason skipReason,
        WindowSafeIdentity identity,
        string? ruleId = null,
        string? targetDesktopKey = null) =>
        new(
            DateTimeOffset.UtcNow,
            EventSequence: 7,
            WindowEventKind.Shown,
            WindowHandle: (nint)404,
            outcome,
            skipReason,
            ruleId,
            targetDesktopKey,
            identity);
}
