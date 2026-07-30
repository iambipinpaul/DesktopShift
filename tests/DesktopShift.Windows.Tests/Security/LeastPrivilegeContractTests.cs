using System.Reflection;
using System.Text.RegularExpressions;
using DesktopShift.Core.Observation;
using DesktopShift.Windows.Observation;
using Microsoft.Win32.SafeHandles;

namespace DesktopShift.Windows.Tests.Security;

/// <summary>
/// Pins the rights DesktopShift asks Windows for when it opens another process.
/// </summary>
/// <remarks>
/// <para>
/// Classification and identity resolution need an application's image file name,
/// its package family name, and its application user model id. Every one of
/// those is answerable under <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which is
/// also the right most likely to be granted across an integrity boundary. Asking
/// for more would gain nothing and would make DesktopShift look like something
/// it is not.
/// </para>
/// <para>
/// Widening an access mask is a one-character edit, so the mask is asserted from
/// two directions: what the resolver actually passes at run time, and what the
/// shipping source is allowed to pass at all.
/// </para>
/// </remarks>
[TestClass]
public sealed class LeastPrivilegeContractTests
{
    /// <summary>
    /// The argument forms an <c>OpenProcess</c> call site may use: the named
    /// least-privilege constant, the parameter a wrapper forwards, or the type
    /// name that starts a declaration.
    /// </summary>
    private static readonly string[] AllowedOpenProcessArguments =
        [
            "ProcessAccessRights.QueryLimitedInformation",
            "QueryLimitedInformation",
            "desiredAccess",
            "uint",
        ];

    [TestMethod]
    public async Task IdentityResolver_OpensAProcessForNothingBeyondTheLimitedQueryRight()
    {
        RecordingProcessApi processApi = new();
        WindowsProcessIdentityResolver resolver = new(
            new StubWindowApi(),
            processApi);

        WindowIdentityResolution resolution = await resolver.ResolveAsync(
            new QualifiedWindow((nint)7, (nint)7, 4242, "ApplicationWindow"));

        Assert.IsTrue(resolution.IsSuccessful);
        Assert.AreEqual(1, processApi.OpenCount);

        // 0x1000 is PROCESS_QUERY_LIMITED_INFORMATION, written out so a typo in
        // the named constant fails here rather than silently widening the mask.
        Assert.AreEqual(0x1000U, processApi.RequestedAccess);
        Assert.AreEqual(
            ProcessAccessRights.QueryLimitedInformation,
            processApi.RequestedAccess);

        // Stated as a mask, not only as a value: any extra right that were ever
        // ORed in would survive an equality check written the other way round.
        Assert.AreEqual(
            0U,
            processApi.RequestedAccess & ~ProcessAccessRights.QueryLimitedInformation,
            "A right beyond the limited query right was requested.");
    }

    [TestMethod]
    public async Task IdentityResolver_ByDefaultBuildsNoCommandLineReaderAtAll()
    {
        // The command line is not part of the identity rules match on, and it
        // can carry a path, a URL, or a credential. The shipping configuration
        // therefore never constructs the reader that would query one, so the
        // capability is absent rather than merely unused.
        WindowsProcessIdentityResolver resolver = new(
            new StubWindowApi(),
            new RecordingProcessApi());

        object? reader = typeof(WindowsProcessIdentityResolver)
            .GetField(
                "commandLineReader",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resolver);

        Assert.IsInstanceOfType<UnavailableProcessCommandLineReader>(reader);

        WindowIdentityResolution resolution = await resolver.ResolveAsync(
            new QualifiedWindow((nint)7, (nint)7, 4242, "ApplicationWindow"));

        Assert.IsNull(resolution.Identity!.CommandLine);
        Assert.IsNull(resolution.Identity.WindowTitle);
    }

    [TestMethod]
    public async Task CommandLineReader_WhenOptedIn_StillAsksForOnlyTheLimitedQueryRight()
    {
        RecordingCommandLineApi nativeApi = new();
        WindowsProcessCommandLineReader reader = new(nativeApi);

        string? commandLine = await reader.TryReadAsync(4242);

        Assert.IsNull(commandLine);
        Assert.AreEqual(
            ProcessAccessRights.QueryLimitedInformation,
            nativeApi.RequestedAccess);
    }

    [TestMethod]
    public void EveryOpenProcessCallSite_PassesOnlyTheLimitedQueryRight()
    {
        string? root = RepositorySource.TryFindRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return;
        }

        List<string> violations = [];
        int callSites = 0;

        foreach (SourceFile source in RepositorySource.ReadShippingSources(root))
        {
            foreach (Match match in Regex.Matches(
                source.Code,
                @"OpenProcess\s*\(\s*([A-Za-z_][A-Za-z0-9_.]*|0[xX][0-9A-Fa-f]+|[0-9]+)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)))
            {
                callSites++;
                string argument = match.Groups[1].Value;
                if (!AllowedOpenProcessArguments.Contains(
                    argument,
                    StringComparer.Ordinal))
                {
                    violations.Add(
                        $"{source.RelativePath}: OpenProcess({argument}, …)");
                }
            }
        }

        Assert.IsGreaterThan(
            0,
            callSites,
            "No OpenProcess use was found, so the scan proved nothing.");
        Assert.IsEmpty(
            violations,
            "A process handle was requested with something other than the " +
            "named limited query right:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private sealed class RecordingProcessApi : IWindowsProcessIdentityApi
    {
        public uint RequestedAccess { get; private set; }

        public int OpenCount { get; private set; }

        public SafeProcessHandle OpenProcess(uint desiredAccess, uint processId)
        {
            OpenCount++;
            RequestedAccess = desiredAccess;
            return new SafeProcessHandle((nint)1, ownsHandle: false);
        }

        public string? TryGetExecutablePath(SafeProcessHandle process) =>
            @"C:\Apps\Code.exe";

        public string? TryGetPackageFamilyName(SafeProcessHandle process) => null;

        public string? TryGetAppUserModelId(SafeProcessHandle process) => null;
    }

    private sealed class RecordingCommandLineApi : IWindowsCommandLineApi
    {
        public uint RequestedAccess { get; private set; }

        public SafeProcessHandle OpenProcess(uint desiredAccess, uint processId)
        {
            RequestedAccess = desiredAccess;

            // An invalid handle stands in for the denial an unelevated caller
            // gets, so the reader gives up instead of querying anything.
            return new SafeProcessHandle((nint)0, ownsHandle: false);
        }

        public int QueryCommandLine(
            SafeProcessHandle process,
            nint buffer,
            uint bufferLength,
            out uint requiredLength)
        {
            requiredLength = 0;
            Assert.Fail("A denied process handle must never be queried.");
            return 0;
        }
    }

    private sealed class StubWindowApi : IWindowsWindowNativeApi
    {
        public bool IsWindow(nint windowHandle) => true;

        public bool IsWindowVisible(nint windowHandle) => true;

        public nint GetRootOwner(nint windowHandle) => windowHandle;

        public nint GetOwner(nint windowHandle) => 0;

        public long GetWindowStyle(nint windowHandle) => 0;

        public long GetWindowExtendedStyle(nint windowHandle) => 0;

        public bool IsCloaked(nint windowHandle) => false;

        public nint GetShellWindow() => 0;

        public nint GetDesktopWindow() => 0;

        public uint GetWindowProcessId(nint windowHandle) => 4242;

        public string GetWindowClass(nint windowHandle) => "ApplicationWindow";

        public string? GetWindowTitle(nint windowHandle)
        {
            Assert.Fail("A window title must never be read by default.");
            return null;
        }

        public void EnumerateChildWindows(
            nint windowHandle,
            Func<nint, bool> onChild)
        {
        }
    }
}
