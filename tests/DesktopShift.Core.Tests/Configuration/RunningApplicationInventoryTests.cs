using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Configuration;

/// <summary>
/// Selecting a running application, and proving a candidate rule against the
/// windows that are open.
/// </summary>
[TestClass]
public sealed class RunningApplicationInventoryTests
{
    [TestMethod]
    public async Task RunningWindows_AreOfferedOncePerApplicationWithEveryDetectedSignal()
    {
        FakeIconReader icons = new();
        icons.Add(
            @"C:\Program Files\WindowsApps\Terminal\WindowsTerminal.exe",
            new ApplicationIcon(2, 1, [.. new byte[8]]));

        RunningApplicationInventory inventory = CreateInventory(
            icons,
            Terminal(1, "CASCADIA_HOSTING_WINDOW_CLASS"),
            Terminal(2, "CASCADIA_HOSTING_WINDOW_CLASS"),
            Terminal(3, "Windows.UI.Composition.DesktopWindowContentBridge"),
            Code(4));

        RunningApplicationSnapshot snapshot = await inventory.ReadAsync();

        Assert.HasCount(4, snapshot.Windows);
        Assert.HasCount(2, snapshot.Applications);

        RunningApplicationCandidate code = snapshot.Applications[0];
        Assert.AreEqual("Code", code.DisplayName);
        Assert.IsFalse(code.HasPackagedIdentity);
        Assert.AreEqual(1, code.WindowCount);
        Assert.IsNull(code.Icon);

        RunningApplicationCandidate terminal = snapshot.Applications[1];
        Assert.AreEqual("WindowsTerminal", terminal.DisplayName);
        Assert.IsTrue(terminal.HasPackagedIdentity);
        Assert.AreEqual(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            terminal.PackageFamilyName);
        Assert.AreEqual(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            terminal.AppUserModelId);
        Assert.AreEqual(3, terminal.WindowCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "CASCADIA_HOSTING_WINDOW_CLASS",
                "Windows.UI.Composition.DesktopWindowContentBridge",
            },
            terminal.WindowClasses.ToArray());
        Assert.IsNotNull(terminal.Icon);
        Assert.Contains("3 windows", terminal.Summary, StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            terminal.Summary,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WindowsTheObserverWouldSkip_AreNeverOffered()
    {
        // Offering an application the classifier skips would be offering a rule
        // that silently never runs.
        FakeIconReader icons = new();
        FakeWindowEnumerator enumerator = new([1, 2, 3]);
        FakeClassifier classifier = new();
        FakeIdentityResolver resolver = new();

        classifier.Skip(1, WindowSkipReason.ToolWindow);
        classifier.Qualify(2, Code(2));
        classifier.Qualify(3, SystemUi(3));
        classifier.SkipIdentity("dwm.exe", WindowSkipReason.SystemWindow);
        resolver.Add(2, Code(2).Identity);
        resolver.Add(3, SystemUi(3).Identity);

        RunningApplicationSnapshot snapshot =
            await new RunningApplicationInventory(
                enumerator,
                classifier,
                resolver,
                icons).ReadAsync();

        Assert.HasCount(1, snapshot.Applications);
        Assert.AreEqual("Code.exe", snapshot.Applications[0].ProcessName);
    }

    [TestMethod]
    public async Task RunningApplicationCandidates_CarryNoWindowTitle()
    {
        // The picker is not an activity projection, but a window title is still
        // the one signal that routinely names a document, an account, or a URL,
        // and it is not needed to identify an application.
        RunningApplicationInventory inventory = CreateInventory(
            new FakeIconReader(),
            Code(1) with
            {
                Identity = Code(1).Identity with
                {
                    WindowTitle = "secret-plan.docx - Visual Studio Code",
                },
            });

        RunningApplicationSnapshot snapshot = await inventory.ReadAsync();

        Assert.HasCount(1, snapshot.Applications);
        foreach (string text in new[]
        {
            snapshot.Applications[0].DisplayName,
            snapshot.Applications[0].Summary,
        })
        {
            Assert.DoesNotContain("secret-plan", text, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void TestingARule_ReportsTheWindowsItWouldClaimAndTheSignalItUsed()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule terminal =
            ApplicationRuleCatalog.Find(document, "windows-terminal")!;

        ApplicationRuleTestResult result = ApplicationRuleTester.Test(
            terminal,
            [
                Terminal(1, "CASCADIA_HOSTING_WINDOW_CLASS").Identity,
                Terminal(2, "CASCADIA_HOSTING_WINDOW_CLASS").Identity,
                Code(3).Identity,
            ]);

        Assert.AreEqual(3, result.EvaluatedWindowCount);
        Assert.AreEqual(2, result.MatchCount);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, result.StrongestSignal);
        Assert.IsTrue(result.IsRuleEnabled);
        Assert.IsTrue(result.SupportsManualReassignment);
    }

    [TestMethod]
    public void TestingADisabledRuleWithoutTheManualTrigger_StillReportsItsMatches()
    {
        // A test answers "which windows does this identity describe", so it must
        // not be silenced by the two things that decide when the rule runs.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule rule = ApplicationRuleCatalog.Find(document, "vscode")! with
        {
            IsEnabled = false,
            Triggers = [ApplicationRuleTrigger.WindowCreated],
        };

        ApplicationRuleTestResult result = ApplicationRuleTester.Test(
            rule,
            [Code(1).Identity]);

        Assert.AreEqual(1, result.MatchCount);
        Assert.AreEqual(WindowMatchStrength.ProcessName, result.StrongestSignal);
        Assert.IsFalse(result.IsRuleEnabled);
        Assert.IsFalse(result.SupportsManualReassignment);
    }

    [TestMethod]
    public void TestingARule_HonorsAWindowClassRefinement()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule rule =
            ApplicationRuleCatalog.Find(document, "remote-desktop")! with
            {
                WindowClasses = ["TscShellContainerClass"],
            };

        ApplicationRuleTestResult result = ApplicationRuleTester.Test(
            rule,
            [
                RemoteDesktop(1, "TscShellContainerClass"),
                RemoteDesktop(2, "#32770"),
            ]);

        Assert.AreEqual(1, result.MatchCount);

        // The class narrowed which windows matched; the process name is still
        // what identified the application, so it is still the reported signal.
        Assert.AreEqual(WindowMatchStrength.ProcessName, result.StrongestSignal);
        Assert.AreEqual(
            "TscShellContainerClass",
            result.Matches[0].Identity.WindowClass);
    }

    [TestMethod]
    public void TestingARule_ReportsOnlyPrivacySafeIdentity()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        WindowIdentity identity = Code(1).Identity with
        {
            WindowTitle = "quarterly-results.xlsx",
            CommandLine = @"Code.exe --user-data-dir=C:\Users\someone\profile",
        };

        ApplicationRuleTestResult result = ApplicationRuleTester.Test(
            ApplicationRuleCatalog.Find(document, "vscode")!,
            [identity]);

        Assert.AreEqual(1, result.MatchCount);
        WindowSafeIdentity safe = result.Matches[0].Identity;
        Assert.AreEqual("Code.exe", safe.ProcessName);
        Assert.AreEqual("Chrome_WidgetWin_1", safe.WindowClass);
    }

    private static RunningApplicationInventory CreateInventory(
        IApplicationIconReader iconReader,
        params QualifiedWindowIdentity[] windows)
    {
        FakeWindowEnumerator enumerator =
            new([.. windows.Select(static window => window.Window.OriginalWindowHandle)]);
        FakeClassifier classifier = new();
        FakeIdentityResolver resolver = new();

        foreach (QualifiedWindowIdentity window in windows)
        {
            classifier.Qualify(window.Window.OriginalWindowHandle, window);
            resolver.Add(window.Window.OriginalWindowHandle, window.Identity);
        }

        return new RunningApplicationInventory(
            enumerator,
            classifier,
            resolver,
            iconReader);
    }

    private static QualifiedWindowIdentity Terminal(
        nint handle,
        string windowClass) =>
        new(
            new QualifiedWindow(handle, handle, 100, windowClass),
            new WindowIdentity(
                100,
                "WindowsTerminal.exe",
                @"C:\Program Files\WindowsApps\Terminal\WindowsTerminal.exe",
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                windowClass,
                WindowTitle: null,
                CommandLine: null));

    private static QualifiedWindowIdentity Code(nint handle) =>
        new(
            new QualifiedWindow(handle, handle, 200, "Chrome_WidgetWin_1"),
            new WindowIdentity(
                200,
                "Code.exe",
                @"C:\Program Files\Microsoft VS Code\Code.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Chrome_WidgetWin_1",
                WindowTitle: null,
                CommandLine: null));

    private static QualifiedWindowIdentity SystemUi(nint handle) =>
        new(
            new QualifiedWindow(handle, handle, 300, "Dwm"),
            new WindowIdentity(
                300,
                "dwm.exe",
                @"C:\Windows\System32\dwm.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Dwm",
                WindowTitle: null,
                CommandLine: null));

    private static WindowIdentity RemoteDesktop(
        nint handle,
        string windowClass) =>
        new(
            unchecked((uint)handle) + 400,
            "mstsc.exe",
            @"C:\Windows\System32\mstsc.exe",
            PackageFamilyName: null,
            AppUserModelId: null,
            windowClass,
            WindowTitle: null,
            CommandLine: null);

    private sealed record QualifiedWindowIdentity(
        QualifiedWindow Window,
        WindowIdentity Identity);

    private sealed class FakeWindowEnumerator(ImmutableArray<nint> windows) :
        ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() => windows;
    }

    private sealed class FakeClassifier : IWindowClassifier
    {
        private readonly Dictionary<nint, WindowQualification> qualifications = [];
        private readonly Dictionary<string, WindowSkipReason> identitySkips =
            new(StringComparer.OrdinalIgnoreCase);

        public void Qualify(nint handle, QualifiedWindowIdentity window) =>
            qualifications[handle] = WindowQualification.Qualified(window.Window);

        public void Skip(nint handle, WindowSkipReason reason) =>
            qualifications[handle] = WindowQualification.Skipped(reason);

        public void SkipIdentity(string processName, WindowSkipReason reason) =>
            identitySkips[processName] = reason;

        public WindowQualification Qualify(nint windowHandle) =>
            qualifications.TryGetValue(
                windowHandle,
                out WindowQualification? qualification)
                ? qualification
                : WindowQualification.Skipped(WindowSkipReason.StaleWindow);

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            identitySkips.TryGetValue(
                identity.ProcessName,
                out WindowSkipReason reason)
                ? reason
                : WindowSkipReason.None;
    }

    private sealed class FakeIdentityResolver : IWindowIdentityResolver
    {
        private readonly Dictionary<nint, WindowIdentity> identities = [];

        public void Add(nint handle, WindowIdentity identity) =>
            identities[handle] = identity;

        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                identities.TryGetValue(
                    window.RootWindowHandle,
                    out WindowIdentity? identity)
                    ? WindowIdentityResolution.Succeeded(identity)
                    : WindowIdentityResolution.Failed(
                        WindowIdentityResolutionFailure.AccessDenied));
    }

    private sealed class FakeIconReader : IApplicationIconReader
    {
        private readonly Dictionary<string, ApplicationIcon> icons =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(string executablePath, ApplicationIcon icon) =>
            icons[executablePath] = icon;

        public ApplicationIcon? TryRead(string? executablePath) =>
            executablePath is not null &&
            icons.TryGetValue(executablePath, out ApplicationIcon? icon)
                ? icon
                : null;
    }
}
