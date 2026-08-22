using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tiling;

/// <summary>
/// Uses the existing window classifier and identity resolver for tiling.
/// </summary>
public sealed class QualifiedTilingIdentitySource(
    IWindowClassifier classifier,
    IWindowIdentityResolver identityResolver) : ITilingIdentitySource
{
    public async ValueTask<WindowIdentity?> ResolveAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        WindowQualification qualification = classifier.Qualify(windowHandle);
        if (qualification.Window is not QualifiedWindow window)
        {
            // The assignment pipeline must never assign DesktopShift itself to
            // another virtual desktop. Tiling is different: the visible app
            // window belongs in the current desktop's layout like any other
            // normal top-level window. The classifier has already proved that
            // this is our process, so no process access is needed here.
            if (qualification.SkipReason == WindowSkipReason.DesktopShiftWindow)
            {
                return new WindowIdentity(
                    unchecked((uint)Environment.ProcessId),
                    ProductInfo.ApplicationName,
                    ExecutablePath: null,
                    PackageFamilyName: null,
                    AppUserModelId: null,
                    WindowClass: ProductInfo.ApplicationName,
                    WindowTitle: null,
                    CommandLine: null);
            }

            return null;
        }

        WindowIdentityResolution resolution = await identityResolver
            .ResolveAsync(window, cancellationToken)
            .ConfigureAwait(false);
        return resolution.Identity;
    }
}
