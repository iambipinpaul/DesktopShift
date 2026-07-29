using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Compatibility;

public sealed class CompatibilityCoordinator : ICompatibilityCoordinator
{
    private readonly object syncRoot = new();
    private readonly IDesktopTopologyProvider provider;
    private readonly PrivilegeBoundary privileges;
    private CompatibilityStatus current;

    /// <param name="buildInfoProvider">The Windows build to classify.</param>
    /// <param name="provider">The desktop topology provider to test.</param>
    /// <param name="privilegeProvider">
    /// Reads DesktopShift's own elevation state. It is optional so the
    /// container can construct the coordinator without registering a probe that
    /// only ever describes the current process.
    /// </param>
    public CompatibilityCoordinator(
        IWindowsBuildInfoProvider buildInfoProvider,
        IDesktopTopologyProvider provider,
        IProcessPrivilegeProvider? privilegeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(buildInfoProvider);
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
        privileges = PrivilegeBoundary.ForProcess(
            (privilegeProvider ?? new WindowsProcessPrivilegeProvider())
                .IsCurrentProcessElevated());

        WindowsBuildInfo build = buildInfoProvider.GetCurrent();
        WindowsBuildAssessment assessment = WindowsBuildClassifier.Classify(build);
        current = new CompatibilityStatus(
            build,
            assessment,
            new DesktopTopologyProviderState(
                provider.Identity,
                provider.Capabilities,
                DesktopTopologyProviderAvailability.NotTested,
                GetProviderExplanation(provider)),
            CompatibilityTestResult.NotRun,
            privileges);
    }

    public CompatibilityStatus Current
    {
        get
        {
            lock (syncRoot)
            {
                return current;
            }
        }
    }

    public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged;

    public async ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
        CancellationToken cancellationToken = default)
    {
        CompatibilityStatus before = Current;
        ImmutableArray<CompatibilityDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<CompatibilityDiagnostic>();

        diagnostics.Add(BuildDetectedDiagnostic(before));
        diagnostics.Add(PrivilegeBoundaryDiagnostic(privileges));

        DesktopTopologyProviderResult providerResult;

        try
        {
            providerResult = await provider
                .TestCompatibilityAsync(before.Build, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            providerResult = DesktopTopologyProviderResult.Failed(
                "provider.compatibility_test_exception",
                exception.Message,
                exception.HResult);
        }

        CompatibilityStatus testedStatus = before with
        {
            Provider = before.Provider with
            {
                Identity = provider.Identity,
                Capabilities = provider.Capabilities,
            },
            Privileges = privileges,
        };
        diagnostics.Add(ProviderSelectedDiagnostic(testedStatus));
        AddCapabilityDiagnostics(diagnostics, provider.Capabilities);
        AddFallbackDiagnostic(diagnostics, provider);

        CompatibilityTestResult result = CreateTestResult(
            testedStatus,
            providerResult,
            diagnostics);
        DesktopTopologyProviderState providerState = testedStatus.Provider with
        {
            Availability = result.IsSuccessful
                ? DesktopTopologyProviderAvailability.Ready
                : DesktopTopologyProviderAvailability.Failed,
            Explanation = result.Summary,
        };
        CompatibilityStatus after = testedStatus with
        {
            Provider = providerState,
            LastTest = result,
        };

        lock (syncRoot)
        {
            current = after;
        }

        StatusChanged?.Invoke(this, new CompatibilityStatusChangedEventArgs(after));
        return result;
    }

    private static string GetProviderExplanation(IDesktopTopologyProvider provider) =>
        provider.Identity.Mode == DesktopTopologyProviderMode.Limited
            ? "Limited Mode uses only documented Windows APIs. Desktop enumeration, creation, switching, and topology notifications are unavailable."
            : "Full Mode capabilities must be validated for this exact Windows build before use.";

    private static CompatibilityDiagnostic BuildDetectedDiagnostic(CompatibilityStatus status) =>
        CompatibilityDiagnostic.Create(
            "Compatibility.BuildDetected",
            status.BuildAssessment.IsRecognized
                ? CompatibilityDiagnosticSeverity.Information
                : CompatibilityDiagnosticSeverity.Warning,
            status.BuildAssessment.Explanation,
            ("exactVersion", status.Build.ExactVersion),
            ("architecture", status.Build.Architecture.ToString()),
            ("support", status.BuildAssessment.Support.ToString()));

    /// <summary>
    /// States the privileges DesktopShift holds, which windows that leaves out
    /// of reach, and that no elevated companion is implemented or required.
    /// </summary>
    /// <remarks>
    /// This is emitted on every compatibility test, not only when something is
    /// denied. A user who sees an access-denied row in Activity needs the
    /// explanation to already be in the diagnostics they export, rather than
    /// having to reproduce the denial to obtain it.
    /// </remarks>
    /// <param name="privileges">The boundary observed for this process.</param>
    /// <returns>The diagnostic to record.</returns>
    private static CompatibilityDiagnostic PrivilegeBoundaryDiagnostic(
        PrivilegeBoundary privileges) =>
        CompatibilityDiagnostic.Create(
            "Compatibility.PrivilegeBoundary",
            privileges.IsProcessElevated
                ? CompatibilityDiagnosticSeverity.Warning
                : CompatibilityDiagnosticSeverity.Information,
            $"{privileges.Explanation} {privileges.ElevatedCompanionBoundary}",
            ("processElevated", privileges.IsProcessElevated.ToString()),
            ("requestsElevation", bool.FalseString),
            ("elevatedCompanion", "not implemented; not required"));

    private static CompatibilityDiagnostic ProviderSelectedDiagnostic(CompatibilityStatus status) =>
        CompatibilityDiagnostic.Create(
            "Compatibility.ProviderSelected",
            CompatibilityDiagnosticSeverity.Information,
            $"Selected {status.Provider.Identity.DisplayName}.",
            ("providerId", status.Provider.Identity.Id),
            ("providerVersion", status.Provider.Identity.Version),
            ("mode", status.Provider.Identity.Mode.ToString()),
            ("usesPrivateApis", status.Provider.Identity.UsesPrivateApis.ToString()));

    private static void AddCapabilityDiagnostics(
        ImmutableArray<CompatibilityDiagnostic>.Builder diagnostics,
        VirtualDesktopCapabilities capabilities)
    {
        diagnostics.Add(
            CompatibilityDiagnostic.Create(
                "Compatibility.CapabilitiesChecked",
                capabilities.HasPrivateTopologyCapabilities
                    ? CompatibilityDiagnosticSeverity.Warning
                    : CompatibilityDiagnosticSeverity.Information,
                "Virtual-desktop capabilities were evaluated.",
                ("getWindowDesktopId", capabilities.CanGetWindowDesktopId.ToString()),
                ("moveWindowToDesktop", capabilities.CanMoveWindowToDesktop.ToString()),
                ("enumerateDesktops", capabilities.CanEnumerateDesktops.ToString()),
                ("getCurrentDesktop", capabilities.CanGetCurrentDesktop.ToString()),
                ("createDesktop", capabilities.CanCreateDesktop.ToString()),
                ("switchDesktop", capabilities.CanSwitchDesktop.ToString()),
                ("topologyNotifications", capabilities.CanObserveTopologyChanges.ToString())));
    }

    private static void AddFallbackDiagnostic(
        ImmutableArray<CompatibilityDiagnostic>.Builder diagnostics,
        IDesktopTopologyProvider provider)
    {
        if (provider is not IDesktopTopologyFallbackSource { LastFallback: { } fallback })
        {
            return;
        }

        diagnostics.Add(
            new CompatibilityDiagnostic(
                "Compatibility.ProviderFallbackActivated",
                CompatibilityDiagnosticSeverity.Warning,
                fallback.Error.Message,
                ImmutableDictionary<string, string>.Empty
                    .Add("requestedProviderId", fallback.RequestedProviderId)
                    .Add("fallbackProviderId", provider.Identity.Id)
                    .Add("stage", fallback.Stage)
                    .Add("code", fallback.Error.Code),
                fallback.Error.HResult,
                fallback.Error.NativeErrorCode));
    }

    private static CompatibilityTestResult CreateTestResult(
        CompatibilityStatus status,
        DesktopTopologyProviderResult providerResult,
        ImmutableArray<CompatibilityDiagnostic>.Builder diagnostics)
    {
        if (!providerResult.IsSuccess)
        {
            DesktopTopologyProviderError error = providerResult.Error ??
                new DesktopTopologyProviderError(
                    "provider.compatibility_test_failed",
                    "The provider compatibility test failed.");

            diagnostics.Add(
                new CompatibilityDiagnostic(
                    "Compatibility.ProviderCheckFailed",
                    CompatibilityDiagnosticSeverity.Error,
                    error.Message,
                    ImmutableDictionary<string, string>.Empty
                        .Add("providerId", status.Provider.Identity.Id)
                        .Add("code", error.Code),
                    error.HResult,
                    error.NativeErrorCode));

            return new CompatibilityTestResult(
                CompatibilityTestOutcome.Failed,
                DateTimeOffset.UtcNow,
                $"Compatibility test failed: {error.Message}",
                diagnostics.ToImmutable());
        }

        CompatibilityTestOutcome outcome = status.Provider.Identity.Mode switch
        {
            DesktopTopologyProviderMode.Full => CompatibilityTestOutcome.PassedFullMode,
            _ => CompatibilityTestOutcome.PassedLimitedMode,
        };
        string summary = outcome == CompatibilityTestOutcome.PassedLimitedMode
            ? "Limited Mode is ready for desktop membership queries. Cross-process window moves, desktop enumeration, creation, switching, and notifications are not available."
            : "Full Mode is ready for this Windows build.";

        diagnostics.Add(
            CompatibilityDiagnostic.Create(
                "Compatibility.ProviderCheckSucceeded",
                CompatibilityDiagnosticSeverity.Information,
                summary,
                ("providerId", status.Provider.Identity.Id),
                ("outcome", outcome.ToString())));

        return new CompatibilityTestResult(
            outcome,
            DateTimeOffset.UtcNow,
            summary,
            diagnostics.ToImmutable());
    }
}
