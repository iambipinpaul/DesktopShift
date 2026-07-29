using DesktopShift.Core.Recovery;
using DesktopShift.Windows.Recovery;

namespace DesktopShift.Windows.Tests.Recovery;

/// <summary>
/// The whole message-to-signal decision, proved without a window.
/// </summary>
/// <remarks>
/// Nothing here restarts Explorer, suspends or resumes the machine, changes a
/// display setting, creates a window, or broadcasts a message. The mapping was
/// deliberately written as a pure function so that every case — including the
/// ones a machine can only reach by actually going to sleep — can be stated as
/// four numbers and an expected answer.
/// </remarks>
[TestClass]
public sealed class WindowsShellLifecycleMessageMappingTests
{
    /// <summary>
    /// A plausible <c>TaskbarCreated</c> id. <c>RegisterWindowMessage</c> hands
    /// out ids in the range 0xC000 to 0xFFFF, so this stands in for a real one
    /// without registering anything, and it collides with no documented message.
    /// </summary>
    private const uint TaskbarCreated = 0xC137;

    /// <summary><c>WM_NULL</c>, the message a zero id would otherwise swallow.</summary>
    private const uint WmNull = 0x0000;

    /// <summary><c>WM_SETTINGCHANGE</c>, a broadcast this listener ignores.</summary>
    private const uint UnrelatedMessage = 0x001A;

    /// <summary>
    /// Every documented <c>PBT_</c> notification that must not be treated as a
    /// resume, including <c>PBT_APMSUSPEND</c> itself.
    /// </summary>
    /// <remarks>
    /// <c>PBT_APMRESUMECRITICAL</c> (0x0006) is in this list on purpose. It
    /// reads like a resume, but Windows has not delivered it since XP, and
    /// mapping a notification that never arrives would be an untestable claim
    /// about the platform rather than a behaviour.
    /// </remarks>
    private static readonly (nint WParam, string Name)[] NonResumeNotifications =
    [
        (0x0000, "PBT_APMQUERYSUSPEND"),
        (0x0001, "PBT_APMQUERYSTANDBY"),
        (0x0002, "PBT_APMQUERYSUSPENDFAILED"),
        (0x0003, "PBT_APMQUERYSTANDBYFAILED"),
        (0x0004, "PBT_APMSUSPEND"),
        (0x0005, "PBT_APMSTANDBY"),
        (0x0006, "PBT_APMRESUMECRITICAL"),
        (0x0008, "PBT_APMRESUMESTANDBY"),
        (0x0009, "PBT_APMBATTERYLOW"),
        (0x000A, "PBT_APMPOWERSTATUSCHANGE"),
        (0x000B, "PBT_APMOEMEVENT"),
        (0x8013, "PBT_POWERSETTINGCHANGE"),
    ];

    [TestMethod]
    public void TheRegisteredTaskbarCreatedMessage_ReportsAnExplorerRestart()
    {
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            TaskbarCreated,
            wParam: 0,
            TaskbarCreated,
            out ShellLifecycleSignal signal);

        Assert.IsTrue(mapped);
        Assert.AreEqual(ShellLifecycleSignal.ExplorerRestarted, signal);
    }

    [TestMethod]
    public void AnAutomaticResume_ReportsASessionResume()
    {
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            WindowsShellLifecycleMessages.PowerBroadcast,
            WindowsShellLifecycleMessages.ResumeAutomatic,
            TaskbarCreated,
            out ShellLifecycleSignal signal);

        Assert.IsTrue(mapped);
        Assert.AreEqual(ShellLifecycleSignal.SessionResumed, signal);
    }

    [TestMethod]
    public void TheLaterUserResumeNotification_DoesNotReportTheSameWakeTwice()
    {
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            WindowsShellLifecycleMessages.PowerBroadcast,
            WindowsShellLifecycleMessages.ResumeSuspend,
            TaskbarCreated,
            out _);

        Assert.IsFalse(mapped);
    }

    [TestMethod]
    public void GoingToSleep_ReportsNothing()
    {
        // Only coming back is a recovery trigger. A machine on its way down has
        // nothing to re-register, and answering the suspend would put work in
        // the way of it suspending.
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            WindowsShellLifecycleMessages.PowerBroadcast,
            WindowsShellLifecycleMessages.Suspend,
            TaskbarCreated,
            out _);

        Assert.IsFalse(mapped);
    }

    [TestMethod]
    public void EveryOtherPowerNotification_ReportsNothing()
    {
        List<string> unexpected = [];

        foreach ((nint wParam, string name) in NonResumeNotifications)
        {
            if (WindowsShellLifecycleMessages.TryMap(
                WindowsShellLifecycleMessages.PowerBroadcast,
                wParam,
                TaskbarCreated,
                out ShellLifecycleSignal signal))
            {
                unexpected.Add($"{name} was reported as {signal}.");
            }
        }

        Assert.IsEmpty(
            unexpected,
            "A power notification that says nothing about DesktopShift's " +
            "registrations was treated as a disruption:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, unexpected));
    }

    [TestMethod]
    public void ADisplayChange_ReportsADisplayChange()
    {
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            WindowsShellLifecycleMessages.DisplayChange,
            wParam: 32,
            TaskbarCreated,
            out ShellLifecycleSignal signal);

        Assert.IsTrue(mapped);
        Assert.AreEqual(ShellLifecycleSignal.DisplayChanged, signal);
    }

    [TestMethod]
    public void AnUnrelatedMessage_ReportsNothing()
    {
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            UnrelatedMessage,
            wParam: 0,
            TaskbarCreated,
            out _);

        Assert.IsFalse(mapped);
    }

    [TestMethod]
    public void AnUnregisteredTaskbarCreatedMessage_NeverSwallowsWmNull()
    {
        // RegisterWindowMessage returns zero when it fails, and zero is WM_NULL
        // — a message every window receives routinely. Matching on it would
        // report an Explorer restart several times a second.
        bool mapped = WindowsShellLifecycleMessages.TryMap(
            WmNull,
            wParam: 0,
            taskbarCreatedMessage: 0,
            out _);

        Assert.IsFalse(mapped);

        // An unregistered id must cost only the Explorer signal, not the other
        // two: those are documented constants and do not depend on it.
        Assert.IsTrue(
            WindowsShellLifecycleMessages.TryMap(
                WindowsShellLifecycleMessages.DisplayChange,
                wParam: 0,
                taskbarCreatedMessage: 0,
                out ShellLifecycleSignal display));
        Assert.AreEqual(ShellLifecycleSignal.DisplayChanged, display);

        Assert.IsTrue(
            WindowsShellLifecycleMessages.TryMap(
                WindowsShellLifecycleMessages.PowerBroadcast,
                WindowsShellLifecycleMessages.ResumeAutomatic,
                taskbarCreatedMessage: 0,
                out ShellLifecycleSignal resume));
        Assert.AreEqual(ShellLifecycleSignal.SessionResumed, resume);
    }

    [TestMethod]
    public void ATaskbarCreatedIdThatCollidesWithNothing_LeavesTheOtherMessagesAlone()
    {
        Assert.IsTrue(
            WindowsShellLifecycleMessages.TryMap(
                TaskbarCreated,
                wParam: 0,
                TaskbarCreated,
                out ShellLifecycleSignal explorer));
        Assert.AreEqual(ShellLifecycleSignal.ExplorerRestarted, explorer);

        Assert.IsTrue(
            WindowsShellLifecycleMessages.TryMap(
                WindowsShellLifecycleMessages.DisplayChange,
                wParam: 0,
                TaskbarCreated,
                out ShellLifecycleSignal display));
        Assert.AreEqual(ShellLifecycleSignal.DisplayChanged, display);

        Assert.IsTrue(
            WindowsShellLifecycleMessages.TryMap(
                WindowsShellLifecycleMessages.PowerBroadcast,
                WindowsShellLifecycleMessages.ResumeAutomatic,
                TaskbarCreated,
                out ShellLifecycleSignal resume));
        Assert.AreEqual(ShellLifecycleSignal.SessionResumed, resume);

        Assert.IsFalse(
            WindowsShellLifecycleMessages.TryMap(
                WmNull,
                wParam: 0,
                TaskbarCreated,
                out _));
    }

    [TestMethod]
    public void EveryLifecycleSignal_IsReachableFromSomeMessage()
    {
        // A signal nothing can produce is a recovery path nothing can reach, so
        // a newly added member fails here until a message is mapped to it.
        (uint Message, nint WParam)[] deliveries =
        [
            (TaskbarCreated, 0),
            (WindowsShellLifecycleMessages.PowerBroadcast,
                WindowsShellLifecycleMessages.ResumeAutomatic),
            (WindowsShellLifecycleMessages.DisplayChange, 0),
        ];

        HashSet<ShellLifecycleSignal> produced = [];
        foreach ((uint message, nint wParam) in deliveries)
        {
            if (WindowsShellLifecycleMessages.TryMap(
                message,
                wParam,
                TaskbarCreated,
                out ShellLifecycleSignal signal))
            {
                _ = produced.Add(signal);
            }
        }

        CollectionAssert.AreEquivalent(
            Enum.GetValues<ShellLifecycleSignal>(),
            produced.ToArray());
    }
}
