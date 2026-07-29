using System.Text.RegularExpressions;

namespace DesktopShift.Windows.Tests.Security;

/// <summary>
/// One technique DesktopShift refuses to use, and the reason it refuses.
/// </summary>
/// <param name="Name">What the technique is called, for the failure message.</param>
/// <param name="Pattern">
/// The identifier or literal that proves the technique is present. Patterns name
/// APIs and window classes rather than English words, so prose can describe a
/// technique without tripping the scan.
/// </param>
/// <param name="Rationale">Why the technique is refused.</param>
internal sealed record ProhibitedTechnique(
    string Name,
    string Pattern,
    string Rationale);

/// <summary>
/// Fails if any prohibited technique appears in the shipping source.
/// </summary>
/// <remarks>
/// <para>
/// DesktopShift promises to manage windows the way Windows documents and to do
/// nothing invasive to the machine. That promise is worth exactly as much as the
/// evidence behind it, so it is checked against the repository's own files on
/// every test run instead of being asserted in a document.
/// </para>
/// <para>
/// The scan reads code with comments removed, which is what lets the codebase
/// keep explaining — in prose, at the place it matters — why it does not reach
/// for these techniques. Two control tests below prove the scanner detects a
/// real use and ignores a comment that only names one.
/// </para>
/// </remarks>
[TestClass]
public sealed class ProhibitedTechniqueScanTests
{
    private static readonly ProhibitedTechnique[] Techniques =
    [
        new(
            "DLL injection",
            @"\bVirtualAllocEx\b|\bVirtualProtectEx\b|\bNtMapViewOfSection\b|\bLdrLoadDll\b",
            "Writing into another process's address space to load code there."),
        new(
            "remote thread creation",
            @"\bCreateRemoteThread(Ex)?\b|\bNtCreateThreadEx\b|\bRtlCreateUserThread\b|\bQueueUserAPC\b|\bSetThreadContext\b",
            "Running DesktopShift's code inside somebody else's process."),
        new(
            "process memory access",
            @"\b(Read|Write)ProcessMemory\b|\bNt(Read|Write)VirtualMemory\b|\bNtWow64(Read|Write)VirtualMemory64\b",
            "Reading or writing another process's memory. Identity comes from " +
            "documented query APIs instead."),
        new(
            "Explorer patching",
            @"\bSetWindowSubclass\b|\bGWLP?_WNDPROC\b|\bSetClassLongPtr\b",
            "Subclassing or replacing a window procedure DesktopShift does not own."),
        new(
            "taskbar scraping",
            @"\bToolbarWindow32\b|\bTrayNotifyWnd\b|\bSysPager\b|\bReBarWindow32\b|\bNotifyIconOverflowWindow\b|\bTB_(GETBUTTON|BUTTONCOUNT)\b",
            "Reading the shell's own tray controls. DesktopShift adds its icon " +
            "through the documented notification-area API and reads nothing back."),
        new(
            "message hooks",
            @"\bSetWindowsHookEx[AW]?\b|\bWH_(MOUSE_LL|KEYBOARD_LL|CBT|GETMESSAGE|CALLWNDPROC|CALLWNDPROCRET|SHELL|JOURNALRECORD|JOURNALPLAYBACK)\b",
            "Injecting a hook procedure into other processes' message flow. " +
            "Window events come from an out-of-context WinEvent hook, which " +
            "loads nothing into anyone."),
        new(
            "virtual desktop registry fabrication",
            @"Explorer\\{1,2}VirtualDesktops|\bVirtualDesktopIDs\b|\bCurrentVirtualDesktop\b",
            "Writing the shell's private desktop state behind its back. Desktop " +
            "topology is read and changed through the desktop APIs only."),
        new(
            "registry writing",
            @"\bRegSetValue(Ex)?[AW]?\b|\bRegCreateKey(Ex)?[AW]?\b|\bRegDeleteKey(Ex)?[AW]?\b|\bRegistry\.SetValue\b",
            "Changing machine or user registry state. DesktopShift keeps its " +
            "own configuration in its own files under the user's app data."),
        new(
            "elevation and impersonation",
            @"\bAdjustTokenPrivileges\b|\bImpersonateLoggedOnUser\b|\bCreateProcessAsUser[AW]?\b|\bCreateProcessWithTokenW\b|\bLogonUser[AW]?\b|\bDuplicateTokenEx\b|""runas""",
            "Acquiring rights the signed-in user's own token does not already " +
            "carry. DesktopShift runs per-user and asks for nothing more."),
        new(
            "wider process access rights",
            @"\bPROCESS_ALL_ACCESS\b|\bPROCESS_VM_(READ|WRITE|OPERATION)\b|\bPROCESS_QUERY_INFORMATION\b|\bPROCESS_CREATE_THREAD\b|\bPROCESS_DUP_HANDLE\b|\bPROCESS_SUSPEND_RESUME\b",
            "Naming a process right beyond the limited query right classification " +
            "and identity resolution need."),
    ];

    [TestMethod]
    public void ShippingSource_ContainsNoProhibitedTechnique()
    {
        string? root = RepositorySource.TryFindRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return;
        }

        IReadOnlyList<SourceFile> sources =
            RepositorySource.ReadShippingSources(root);
        Assert.IsGreaterThan(
            0,
            sources.Count,
            "No shipping source was found, so the scan proved nothing.");

        List<string> violations = [];
        foreach (SourceFile source in sources)
        {
            foreach (ProhibitedTechnique technique in Techniques)
            {
                foreach (Match match in Regex.Matches(
                    source.Code,
                    technique.Pattern,
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)))
                {
                    violations.Add(
                        $"{source.RelativePath}: '{match.Value}' is " +
                        $"{technique.Name}. {technique.Rationale}");
                }
            }
        }

        Assert.IsEmpty(
            violations,
            "Prohibited techniques found in shipping source:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void Scanner_DetectsAProhibitedTechniqueWrittenAsCode()
    {
        // The control that makes the scan above meaningful: without it, a
        // pattern that silently stopped matching would read as a clean repo.
        const string offending = """
            internal static class Sneaky
            {
                internal static extern nint CreateRemoteThread(nint process);
            }
            """;

        string code = RepositorySource.RemoveComments(offending);

        Assert.IsTrue(
            Techniques.Any(
                technique => Regex.IsMatch(code, technique.Pattern)),
            "The scanner failed to detect a plainly prohibited call.");
    }

    [TestMethod]
    public void Scanner_IgnoresATechniqueNamedOnlyInAComment()
    {
        // DesktopShift documents what it refuses to do, in the files where the
        // refusal matters. A scanner that punished that documentation would
        // push the explanations out of the code.
        const string documented = """
            /// <summary>
            /// Reads identity through documented query APIs. ReadProcessMemory and
            /// CreateRemoteThread are never used, and no WH_MOUSE_LL hook exists.
            /// </summary>
            internal static class Honest
            {
                // SetWindowsHookEx is deliberately absent here.
                internal static int Value => 1; /* VirtualAllocEx is not used */
            }
            """;

        string code = RepositorySource.RemoveComments(documented);

        foreach (ProhibitedTechnique technique in Techniques)
        {
            Assert.IsFalse(
                Regex.IsMatch(code, technique.Pattern),
                $"A comment naming {technique.Name} was treated as a use of it.");
        }
    }

    [TestMethod]
    public void Scanner_ReadsInsideStringLiterals()
    {
        // A banned API reached by name through a string is still the banned API,
        // so literals are deliberately left in place by the comment stripper.
        const string hidden = """
            internal static class Indirect
            {
                internal const string Entry = "CreateRemoteThread";
            }
            """;

        string code = RepositorySource.RemoveComments(hidden);

        Assert.IsTrue(
            Techniques.Any(
                technique => Regex.IsMatch(code, technique.Pattern)),
            "A prohibited API named in a string literal escaped the scan.");
    }
}
