using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Compatibility;

public sealed class CompatibilityCoordinator : ICompatibilityCoordinator
{
    private readonly object syncRoot = new();
    private readonly IDesktopTopologyProvider provider;
    private CompatibilityStatus current;

    public CompatibilityCoordinator(
        IWindowsBuildInfoProvider buildInfoProvider,
        IDesktopTopologyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(buildInfoProvider);
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;

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
            CompatibilityTestResult.NotRun);
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
            ? "Limited Mode is ready. Window moves to known desktop IDs are available; desktop enumeration, creation, switching, and notifications are not."
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
