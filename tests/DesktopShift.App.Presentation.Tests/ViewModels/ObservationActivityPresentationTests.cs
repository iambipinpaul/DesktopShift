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
            targetDesktopKey: "web",
            matchedOn: WindowMatchStrength.ProcessName);

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("Matched", presentation.Outcome);
        Assert.AreEqual(
            "Matched browsers to web by process name",
            presentation.Decision);
        Assert.AreEqual("process name", presentation.MatchSignal);
        Assert.AreEqual("msedge.exe", presentation.ProcessName);
        Assert.Contains(
            "Window class: Chrome_WidgetWin_1",
            presentation.IdentityDetails);
    }

    [TestMethod]
    public void Create_TerminalMatchedOnPackageFamilyNameNamesTheStableIdentity()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            TerminalIdentity,
            ruleId: "terminals",
            targetDesktopKey: "Terminal",
            matchedOn: WindowMatchStrength.PackageFamilyName);

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual(
            "Matched terminals to Terminal by package family name",
            presentation.Decision);
        Assert.AreEqual("package family name", presentation.MatchSignal);
        Assert.Contains(
            "Match signal: package family name",
            presentation.AutomationName);
    }

    [TestMethod]
    public void Create_TerminalMatchedOnProcessNameReadsWeakerThanPackageFamily()
    {
        WindowObservationActivity packaged = CreateActivity(
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            TerminalIdentity,
            ruleId: "terminals",
            targetDesktopKey: "Terminal",
            matchedOn: WindowMatchStrength.PackageFamilyName);
        WindowObservationActivity processOnly = CreateActivity(
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            TerminalIdentity,
            ruleId: "terminals",
            targetDesktopKey: "Terminal",
            matchedOn: WindowMatchStrength.ProcessName);

        ObservationActivityPresentation packagedPresentation =
            ObservationActivityPresentation.Create(packaged);
        ObservationActivityPresentation processPresentation =
            ObservationActivityPresentation.Create(processOnly);

        Assert.AreEqual("process name", processPresentation.MatchSignal);
        Assert.AreEqual(
            "Matched terminals to Terminal by process name",
            processPresentation.Decision);
        Assert.AreNotEqual(
            packagedPresentation.MatchSignal,
            processPresentation.MatchSignal);
        Assert.AreNotEqual(
            packagedPresentation.Decision,
            processPresentation.Decision);
    }

    [TestMethod]
    public void Create_MatchedWithoutRecordedSignalKeepsTheOriginalDecision()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            TerminalIdentity,
            ruleId: "terminals",
            targetDesktopKey: "Terminal");

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("Matched terminals to Terminal", presentation.Decision);
        Assert.AreEqual("No match signal recorded", presentation.MatchSignal);
    }

    [TestMethod]
    public void Create_SkippedSurfaceHasNoMatchSignal()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Skipped,
            WindowSkipReason.NoMatchingRule,
            TerminalIdentity);

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("No match signal recorded", presentation.MatchSignal);
        Assert.AreEqual("Skipped: No Matching Rule", presentation.Decision);
    }

    [TestMethod]
    public void Create_UnansweredActivation_DoesNotClaimRuleMatchingFailed()
    {
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Skipped,
            WindowSkipReason.ActivationNotSwept,
            TerminalIdentity);

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("Skipped: Activation Not Swept", presentation.Decision);
        Assert.AreEqual("Not evaluated", presentation.Rule);
        Assert.DoesNotContain("No matching rule", presentation.AutomationName);
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

    [TestMethod]
    public void Create_PinnedWindowNamesEveryDesktopAndIgnoresTheStoredKey()
    {
        // A pin rule keeps a stored desktop key that nothing reads, so naming
        // it — or reporting the window as unassigned — would describe a
        // placement the pin never makes.
        WindowObservationActivity activity = CreateActivity(
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            TerminalIdentity,
            ruleId: "music",
            targetDesktopKey: "code",
            matchedOn: WindowMatchStrength.ProcessName,
            destination: WindowRuleDestination.PinnedToAllDesktops);

        ObservationActivityPresentation presentation =
            ObservationActivityPresentation.Create(activity);

        Assert.AreEqual("Every desktop", presentation.TargetDesktop);
        Assert.AreEqual(
            "Matched music to every desktop by process name",
            presentation.Decision);
        Assert.DoesNotContain("code", presentation.Decision);
    }

    private static WindowSafeIdentity TerminalIdentity =>
        new(
            "WindowsTerminal.exe",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            "CASCADIA_HOSTING_WINDOW_CLASS");

    private static WindowObservationActivity CreateActivity(
        WindowObservationOutcome outcome,
        WindowSkipReason skipReason,
        WindowSafeIdentity identity,
        string? ruleId = null,
        string? targetDesktopKey = null,
        WindowMatchStrength? matchedOn = null,
        WindowRuleDestination destination = WindowRuleDestination.ManagedDesktop) =>
        new(
            DateTimeOffset.UtcNow,
            EventSequence: 7,
            WindowEventKind.Shown,
            WindowHandle: (nint)404,
            outcome,
            skipReason,
            ruleId,
            targetDesktopKey,
            identity,
            MatchedOn: matchedOn,
            Destination: destination);
}
