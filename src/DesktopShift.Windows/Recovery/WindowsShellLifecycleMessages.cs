using DesktopShift.Core.Recovery;

namespace DesktopShift.Windows.Recovery;

/// <summary>
/// The Windows messages that mean "something happened that recovery cares
/// about", and the one function that decides which of them mean what.
/// </summary>
/// <remarks>
/// <para>
/// The decision is separated from the window that receives the messages on
/// purpose. Proving the mapping is right otherwise needs a real top-level
/// window, a real message pump, and — for the interesting cases — a real
/// Explorer restart, a real sleep, and a real monitor being unplugged. None of
/// those belong in an automated test, and a mapping nobody can test is a mapping
/// that silently rots. Everything here is a pure function of four values, so the
/// whole decision table can be pinned in a unit test while the window that feeds
/// it stays a thin shell.
/// </para>
/// <para>
/// Every value below is documented in <c>winuser.h</c> and is pinned to that
/// value by <c>WindowsShellLifecycleMessageContractTests</c>. Nothing here reads
/// or changes machine state: these are numbers, and the mapping only ever
/// answers a question.
/// </para>
/// </remarks>
public static class WindowsShellLifecycleMessages
{
    /// <summary><c>WM_DISPLAYCHANGE</c>.</summary>
    public const uint DisplayChange = 0x007E;

    /// <summary><c>WM_POWERBROADCAST</c>.</summary>
    public const uint PowerBroadcast = 0x0218;

    /// <summary>
    /// <c>PBT_APMRESUMEAUTOMATIC</c> — the machine woke, whether or not a user
    /// was the one who woke it.
    /// </summary>
    public const nint ResumeAutomatic = 0x0012;

    /// <summary>
    /// <c>PBT_APMRESUMESUSPEND</c> — the machine woke because a user asked it
    /// to. Windows raises this after <see cref="ResumeAutomatic"/> for the same
    /// physical wake, so it is named here only to pin that it maps to nothing.
    /// Triggering on both would schedule two sequential recovery passes when the
    /// first pass completes before this later notification arrives.
    /// </summary>
    public const nint ResumeSuspend = 0x0007;

    /// <summary>
    /// <c>PBT_APMSUSPEND</c> — the machine is going to sleep. Named so that the
    /// test proving it maps to nothing can say what it is testing.
    /// </summary>
    public const nint Suspend = 0x0004;

    /// <summary>
    /// Decides which disruption, if any, a window message reports.
    /// </summary>
    /// <param name="message">The message the window procedure received.</param>
    /// <param name="wParam">
    /// The message's first parameter. Only <see cref="PowerBroadcast"/> reads
    /// it, and for that message it names the power event.
    /// </param>
    /// <param name="taskbarCreatedMessage">
    /// The message id <c>RegisterWindowMessage("TaskbarCreated")</c> returned.
    /// This is a parameter rather than a constant because the id does not exist
    /// until something registers it: Windows allocates it at run time from the
    /// global atom table, so it differs between sessions and cannot be written
    /// down. Passing it in is also what keeps this function pure and testable.
    /// A zero id — which is what <c>RegisterWindowMessage</c> returns when it
    /// fails — must never match anything, because zero is <c>WM_NULL</c>, a
    /// message every window receives routinely. Treating an unregistered id as a
    /// match would report an Explorer restart every time a menu dismissed
    /// itself.
    /// </param>
    /// <param name="signal">The disruption reported, when there is one.</param>
    /// <returns>
    /// <c>true</c> when the message reports a disruption recovery should answer.
    /// </returns>
    public static bool TryMap(
        uint message,
        nint wParam,
        uint taskbarCreatedMessage,
        out ShellLifecycleSignal signal)
    {
        if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage)
        {
            signal = ShellLifecycleSignal.ExplorerRestarted;
            return true;
        }

        if (message == PowerBroadcast)
        {
            // Only coming back counts. Going to sleep is not a recovery trigger:
            // there is nothing to re-register on a machine that is about to stop
            // running, and reacting to the suspend would put a burst of COM
            // calls in the way of the machine trying to go down. Every other
            // power notification — battery, power-source, OEM, setting changes —
            // says nothing about whether DesktopShift's registrations are still
            // real, so it maps to nothing too.
            // PBT_APMRESUMEAUTOMATIC is delivered for every resume.
            // PBT_APMRESUMESUSPEND may follow it for the same physical wake
            // after user interaction, so mapping both would report one wake
            // twice when the notifications are not concurrent.
            if (wParam == ResumeAutomatic)
            {
                signal = ShellLifecycleSignal.SessionResumed;
                return true;
            }

            signal = default;
            return false;
        }

        if (message == DisplayChange)
        {
            signal = ShellLifecycleSignal.DisplayChanged;
            return true;
        }

        signal = default;
        return false;
    }
}
