using DesktopShift.Core.Compatibility;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// Moves the foreground to the desktop sitting at a given position.
/// </summary>
/// <remarks>
/// <para>
/// The switch goes through <see cref="IDesktopTopologyProvider.SwitchDesktopAsync"/>,
/// which names the desktop it wants. DesktopShift deliberately does not
/// synthesize Win+Ctrl+Left or Win+Ctrl+Right to walk there: a synthesized walk
/// takes a different number of steps depending on where the user already is,
/// races anything else moving desktops at the same time, animates once per step,
/// and lands somewhere unpredictable if a desktop is created or removed
/// mid-walk. Naming the destination has none of those failure modes.
/// </para>
/// <para>
/// Position is resolved on every press rather than cached. Desktops are created
/// and removed while DesktopShift runs, and a cached third desktop would send
/// the user somewhere the digit no longer means.
/// </para>
/// <para>
/// Presses are serialized. Leaning on Ctrl+Alt+4 while a switch is still in
/// flight would otherwise stack shell transitions behind each other and keep
/// moving the user after they let go.
/// </para>
/// </remarks>
public sealed class DesktopSwitchShortcutService : IDesktopSwitchShortcutService
{
    private readonly IDesktopTopologyProvider _topologyProvider;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    public DesktopSwitchShortcutService(IDesktopTopologyProvider topologyProvider)
    {
        ArgumentNullException.ThrowIfNull(topologyProvider);
        _topologyProvider = topologyProvider;
    }

    public async Task<DesktopSwitchShortcutResult> SwitchToDesktopAsync(
        int desktopOrdinal,
        CancellationToken cancellationToken = default)
    {
        if (desktopOrdinal is < DesktopSwitchShortcuts.MinDesktopOrdinal
            or > DesktopSwitchShortcuts.MaxDesktopOrdinal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desktopOrdinal),
                desktopOrdinal,
                "The desktop position is outside the range a shortcut can select.");
        }

        if (!_topologyProvider.Capabilities.CanEnumerateDesktops ||
            !_topologyProvider.Capabilities.CanSwitchDesktop)
        {
            return new DesktopSwitchShortcutResult(
                DesktopSwitchShortcutOutcome.Unsupported,
                desktopOrdinal,
                0,
                "DesktopShift cannot switch desktops on this machine. Its virtual desktop provider is running in Limited Mode.");
        }

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>
                enumerated = await _topologyProvider
                    .EnumerateDesktopsAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!enumerated.IsSuccess || enumerated.Value is null)
            {
                return new DesktopSwitchShortcutResult(
                    DesktopSwitchShortcutOutcome.Failed,
                    desktopOrdinal,
                    0,
                    $"DesktopShift could not read the desktop list, so it could not switch to Desktop {desktopOrdinal}.");
            }

            IReadOnlyList<VirtualDesktopDescriptor> desktops = enumerated.Value;
            if (desktopOrdinal > desktops.Count)
            {
                return new DesktopSwitchShortcutResult(
                    DesktopSwitchShortcutOutcome.DesktopMissing,
                    desktopOrdinal,
                    desktops.Count,
                    DescribeMissingDesktop(desktopOrdinal, desktops.Count));
            }

            VirtualDesktopDescriptor target = desktops[desktopOrdinal - 1];
            if (target.IsCurrent)
            {
                // Not a failure and not worth a balloon: the user is already
                // looking at what they asked for.
                return new DesktopSwitchShortcutResult(
                    DesktopSwitchShortcutOutcome.AlreadyCurrent,
                    desktopOrdinal,
                    desktops.Count);
            }

            DesktopTopologyProviderResult switched = await _topologyProvider
                .SwitchDesktopAsync(target.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!switched.IsSuccess)
            {
                return new DesktopSwitchShortcutResult(
                    switched.Outcome == DesktopTopologyResultOutcome.Unsupported
                        ? DesktopSwitchShortcutOutcome.Unsupported
                        : DesktopSwitchShortcutOutcome.Failed,
                    desktopOrdinal,
                    desktops.Count,
                    $"DesktopShift could not switch to Desktop {desktopOrdinal}. {switched.Error?.Message ?? "Windows refused the switch."}");
            }

            return new DesktopSwitchShortcutResult(
                DesktopSwitchShortcutOutcome.Switched,
                desktopOrdinal,
                desktops.Count);
        }
        finally
        {
            _ = _switchGate.Release();
        }
    }

    /// <summary>
    /// Says what is missing and what exists, because "Desktop 7 does not exist"
    /// on its own leaves the user counting their desktops to find out what they
    /// should have pressed.
    /// </summary>
    private static string DescribeMissingDesktop(int desktopOrdinal, int desktopCount) =>
        desktopCount == 1
            ? $"There is no Desktop {desktopOrdinal}. You have one desktop, so only the Desktop 1 shortcut does anything."
            : $"There is no Desktop {desktopOrdinal}. You have {desktopCount} desktops, so the shortcuts for Desktop 1 to {desktopCount} are the ones that work.";
}
