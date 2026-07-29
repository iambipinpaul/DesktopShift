using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Windows.Observation;

namespace DesktopShift.Windows.Tests.Assignments;

/// <summary>
/// Exercises the real Win32 window APIs against windows this test owns.
/// </summary>
/// <remarks>
/// A Remote Desktop session cannot be created inside a test, but the Win32
/// shapes it produces can. These tests stand up a real session frame and a real
/// owned dialog and drive the shipping classifier and matcher over them, so the
/// ownership rule that keeps a reconnection or credential prompt with its
/// session is verified against Windows rather than against a fake.
/// <para>
/// Nothing here moves a window between virtual desktops, so no opt-in is
/// required. The windows never take focus and are destroyed with the test.
/// </para>
/// </remarks>
[TestClass]
public sealed class RemoteDesktopControlledWindowTests
{
    private const string SessionWindowClass = "TscShellContainerClass";
    private const string OwnedDialogWindowClass = "DesktopShift.OwnedDialog";
    private const string RemoteDesktopProcessName = "mstsc.exe";

    // The classifier drops windows belonging to its own process, which is the
    // right rule in production and the wrong one for a window a test created.
    // A process id no process can have keeps the production rule intact while
    // letting the controlled window through.
    private const uint ForeignProcessId = uint.MaxValue;

    private static readonly IReadOnlyList<WindowObservationRule> DefaultRules =
        new ConfigurationWindowRuleSource(ConfigurationDefaults.Create).GetRules();

    [TestMethod]
    public void Qualify_RealOwnedDialog_ResolvesToItsRealSessionFrame()
    {
        using TestTopLevelWindow sessionFrame = new(
            SessionWindowClass,
            preventActivation: true);
        using TestTopLevelWindow ownedDialog = new(
            OwnedDialogWindowClass,
            sessionFrame.Handle,
            preventActivation: true);
        WindowsWindowClassifier classifier = new(
            currentProcessId: ForeignProcessId);

        WindowQualification qualification =
            classifier.Qualify(ownedDialog.Handle);

        Assert.IsTrue(
            qualification.IsQualified,
            $"The owned dialog was skipped as {qualification.SkipReason}.");
        Assert.AreEqual(
            ownedDialog.Handle,
            qualification.Window!.OriginalWindowHandle);

        // The event named the dialog; everything downstream sees the frame.
        Assert.AreEqual(sessionFrame.Handle, qualification.Window.RootWindowHandle);
        Assert.AreEqual(SessionWindowClass, qualification.Window.WindowClass);
    }

    [TestMethod]
    public void OwnedOverlappedDialog_IsOnlyReachableThroughTheOwnerApi()
    {
        // Pins the Win32 behavior the classifier depends on. An owned window
        // without WS_POPUP is invisible to GetAncestor(GA_ROOTOWNER), because
        // that walks what GetParent returns and GetParent reports an owner only
        // for a pop-up. GetWindow(GW_OWNER) sees it. If this ever stops being
        // true the classifier's owner walk can be simplified; while it holds,
        // the walk is what keeps an owned dialog with its session.
        using TestTopLevelWindow sessionFrame = new(
            SessionWindowClass,
            preventActivation: true);
        using TestTopLevelWindow ownedDialog = new(
            OwnedDialogWindowClass,
            sessionFrame.Handle,
            preventActivation: true,
            ownedAsPopup: false);
        WindowsWindowNativeApi nativeApi = new();

        Assert.AreEqual(
            sessionFrame.Handle,
            nativeApi.GetOwner(ownedDialog.Handle));
        Assert.AreNotEqual(
            sessionFrame.Handle,
            nativeApi.GetRootOwner(ownedDialog.Handle));
    }

    [TestMethod]
    public void Qualify_RealOwnedOverlappedDialog_StillResolvesToItsFrame()
    {
        using TestTopLevelWindow sessionFrame = new(
            SessionWindowClass,
            preventActivation: true);
        using TestTopLevelWindow ownedDialog = new(
            OwnedDialogWindowClass,
            sessionFrame.Handle,
            preventActivation: true,
            ownedAsPopup: false);
        WindowsWindowClassifier classifier = new(
            currentProcessId: ForeignProcessId);

        WindowQualification qualification =
            classifier.Qualify(ownedDialog.Handle);

        Assert.IsTrue(
            qualification.IsQualified,
            $"The owned dialog was skipped as {qualification.SkipReason}.");
        Assert.AreEqual(sessionFrame.Handle, qualification.Window!.RootWindowHandle);
        Assert.AreEqual(SessionWindowClass, qualification.Window.WindowClass);
    }

    [TestMethod]
    public void Qualify_RealSessionFrame_IsItsOwnRoot()
    {
        using TestTopLevelWindow sessionFrame = new(
            SessionWindowClass,
            preventActivation: true);
        WindowsWindowClassifier classifier = new(
            currentProcessId: ForeignProcessId);

        WindowQualification qualification =
            classifier.Qualify(sessionFrame.Handle);

        Assert.IsTrue(
            qualification.IsQualified,
            $"The session frame was skipped as {qualification.SkipReason}.");
        Assert.AreEqual(sessionFrame.Handle, qualification.Window!.RootWindowHandle);
        Assert.AreEqual(SessionWindowClass, qualification.Window.WindowClass);
    }

    [TestMethod]
    public void Match_RealOwnedDialog_InheritsTheSessionFramesRule()
    {
        using TestTopLevelWindow sessionFrame = new(
            SessionWindowClass,
            preventActivation: true);
        using TestTopLevelWindow ownedDialog = new(
            OwnedDialogWindowClass,
            sessionFrame.Handle,
            preventActivation: true);
        WindowsWindowClassifier classifier = new(
            currentProcessId: ForeignProcessId);

        WindowQualification qualification =
            classifier.Qualify(ownedDialog.Handle);
        WindowRuleMatch? match = new WindowRuleMatcher().Match(
            CreateSessionIdentity(qualification.Window!),
            WindowEventKind.Shown,
            DefaultRules);

        // The dialog reaches the Remote rule only through its owner, which is
        // exactly the behavior that keeps a credential prompt with its session
        // even when another process hosts the prompt.
        Assert.AreEqual("remote", match!.Rule.Id);
        Assert.AreEqual("remote", match.Rule.TargetDesktopKey);
    }

    [TestMethod]
    public void Qualify_TwoRealSessionFrames_StayIndependent()
    {
        using TestTopLevelWindow first = new(
            SessionWindowClass,
            preventActivation: true);
        using TestTopLevelWindow second = new(
            $"{SessionWindowClass}.Second",
            preventActivation: true);
        using TestTopLevelWindow firstDialog = new(
            $"{OwnedDialogWindowClass}.First",
            first.Handle,
            preventActivation: true);
        WindowsWindowClassifier classifier = new(
            currentProcessId: ForeignProcessId);

        WindowQualification firstResult = classifier.Qualify(first.Handle);
        WindowQualification secondResult = classifier.Qualify(second.Handle);
        WindowQualification dialogResult = classifier.Qualify(firstDialog.Handle);

        // A dialog belonging to one session never resolves onto the other.
        Assert.AreEqual(first.Handle, firstResult.Window!.RootWindowHandle);
        Assert.AreEqual(second.Handle, secondResult.Window!.RootWindowHandle);
        Assert.AreEqual(first.Handle, dialogResult.Window!.RootWindowHandle);
        Assert.AreNotEqual(
            secondResult.Window.RootWindowHandle,
            dialogResult.Window.RootWindowHandle);
    }

    /// <summary>
    /// Builds the identity the resolver would produce for a qualified window,
    /// with the process name a Remote Desktop session reports.
    /// </summary>
    /// <param name="window">The qualified window.</param>
    /// <returns>The identity the matcher sees.</returns>
    private static WindowIdentity CreateSessionIdentity(QualifiedWindow window) =>
        new(
            window.ProcessId,
            RemoteDesktopProcessName,
            ExecutablePath: null,
            PackageFamilyName: null,
            AppUserModelId: null,
            window.WindowClass,
            WindowTitle: null,
            CommandLine: null);
}
