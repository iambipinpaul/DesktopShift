using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class WindowFloatClassifierTests
{
    private static readonly TilingIdentityRule FloatTerminal = new(
        Id: "float-terminal",
        DisplayName: "Terminal",
        IsEnabled: true,
        ProcessNames: ["WindowsTerminal.exe"]);

    private static readonly TilingIdentityRule IgnoreShell = new(
        Id: "ignore-shell",
        DisplayName: "Shell",
        IsEnabled: true,
        PackageFamilyNames: ["Microsoft.WindowsShell_8wekyb3d8bbwe"]);

    [TestMethod]
    public void UnmatchedWindowTiles()
    {
        Assert.AreEqual(
            TilingDisposition.Tile,
            WindowFloatClassifier.Classify(
                MakeIdentity(processName: "notepad.exe"),
                [FloatTerminal],
                [IgnoreShell]));
    }

    [TestMethod]
    public void FloatRuleMatchesByProcessNameWithoutCaseSensitivity()
    {
        Assert.AreEqual(
            TilingDisposition.Float,
            WindowFloatClassifier.Classify(
                MakeIdentity(processName: "windowsterminal.EXE"),
                [FloatTerminal],
                []));
    }

    [TestMethod]
    public void IgnoreBeatsFloatWhenBothMatch()
    {
        var bothRules = new TilingIdentityRule(
            Id: "both",
            DisplayName: "Both",
            IsEnabled: true,
            ProcessNames: ["app.exe"],
            PackageFamilyNames: ["SomeApp_Publisher"]);

        Assert.AreEqual(
            TilingDisposition.Ignore,
            WindowFloatClassifier.Classify(
                MakeIdentity(processName: "app.exe", packageFamilyName: "SomeApp_Publisher"),
                [bothRules],
                [bothRules]));
    }

    [TestMethod]
    public void DisabledRuleNeverMatches()
    {
        var disabled = FloatTerminal with { IsEnabled = false };

        Assert.AreEqual(
            TilingDisposition.Tile,
            WindowFloatClassifier.Classify(
                MakeIdentity(processName: "WindowsTerminal.exe"),
                [disabled],
                []));
    }

    [TestMethod]
    public void ClassRefinesButNeverCarriesAMatch()
    {
        var classOnly = new TilingIdentityRule(
            Id: "class-only",
            DisplayName: "Class only",
            IsEnabled: true,
            WindowClasses: ["CASCADIA_HOSTING_WINDOW_CLASS"]);
        var refined = new TilingIdentityRule(
            Id: "refined",
            DisplayName: "Refined",
            IsEnabled: true,
            ProcessNames: ["WindowsTerminal.exe"],
            WindowClasses: ["OTHER_CLASS"]);

        WindowIdentity identity = MakeIdentity(
            processName: "WindowsTerminal.exe",
            windowClass: "CASCADIA_HOSTING_WINDOW_CLASS");

        // The class alone names no application.
        Assert.AreEqual(
            TilingDisposition.Tile,
            WindowFloatClassifier.Classify(identity, [classOnly], []));

        // A matching process with a non-matching class does not match either.
        Assert.AreEqual(
            TilingDisposition.Tile,
            WindowFloatClassifier.Classify(identity, [refined], []));
    }

    [TestMethod]
    public void UnresolvableIdentityIsIgnoredNotTiled()
    {
        Assert.AreEqual(
            TilingDisposition.Ignore,
            WindowFloatClassifier.Classify(null, [], []));
    }

    private static WindowIdentity MakeIdentity(
        string processName,
        string? packageFamilyName = null,
        string? windowClass = null)
    {
        return new WindowIdentity(
            ProcessId: 1000,
            ProcessName: processName,
            ExecutablePath: null,
            PackageFamilyName: packageFamilyName,
            AppUserModelId: null,
            WindowClass: windowClass ?? "DEFAULT_CLASS",
            WindowTitle: null,
            CommandLine: null);
    }
}
