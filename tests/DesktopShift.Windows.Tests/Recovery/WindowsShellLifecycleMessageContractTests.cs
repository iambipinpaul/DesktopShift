using System.Reflection;
using DesktopShift.Windows.Recovery;

namespace DesktopShift.Windows.Tests.Recovery;

/// <summary>
/// Pins every Windows message constant recovery depends on to the value the
/// documented headers give it.
/// </summary>
/// <remarks>
/// <para>
/// A wrong constant here fails in the worst possible way: nothing throws,
/// nothing logs, and recovery simply never happens, because the message it is
/// waiting for is a message Windows never sends. The values are therefore
/// written down twice — once in the shipping code and once here, next to the
/// header name they came from — so that a typo has to be made in both places to
/// survive.
/// </para>
/// <para>
/// The values come from <c>winuser.h</c> in the Windows SDK. Nothing in this
/// file sends a message, creates a window, or asks Windows anything at all.
/// </para>
/// </remarks>
[TestClass]
public sealed class WindowsShellLifecycleMessageContractTests
{
    [TestMethod]
    public void EveryMessageConstant_MatchesTheValueTheHeadersDocument()
    {
        Dictionary<string, ulong> documented = new(StringComparer.Ordinal)
        {
            // WM_DISPLAYCHANGE
            [nameof(WindowsShellLifecycleMessages.DisplayChange)] = 0x007E,

            // WM_POWERBROADCAST
            [nameof(WindowsShellLifecycleMessages.PowerBroadcast)] = 0x0218,

            // PBT_APMRESUMEAUTOMATIC
            [nameof(WindowsShellLifecycleMessages.ResumeAutomatic)] = 0x0012,

            // PBT_APMRESUMESUSPEND
            [nameof(WindowsShellLifecycleMessages.ResumeSuspend)] = 0x0007,

            // PBT_APMSUSPEND
            [nameof(WindowsShellLifecycleMessages.Suspend)] = 0x0004,
        };

        // The declared values are read through a widening helper rather than
        // compared against the literals inline. An assertion whose two sides are
        // both compile-time constants is answered by the compiler and proves
        // nothing about the shipping code, which is exactly what MSTEST0032
        // fails the build for.
        Dictionary<string, ulong> declared = new(StringComparer.Ordinal)
        {
            [nameof(WindowsShellLifecycleMessages.DisplayChange)] =
                Widen(WindowsShellLifecycleMessages.DisplayChange),
            [nameof(WindowsShellLifecycleMessages.PowerBroadcast)] =
                Widen(WindowsShellLifecycleMessages.PowerBroadcast),
            [nameof(WindowsShellLifecycleMessages.ResumeAutomatic)] =
                Widen(WindowsShellLifecycleMessages.ResumeAutomatic),
            [nameof(WindowsShellLifecycleMessages.ResumeSuspend)] =
                Widen(WindowsShellLifecycleMessages.ResumeSuspend),
            [nameof(WindowsShellLifecycleMessages.Suspend)] =
                Widen(WindowsShellLifecycleMessages.Suspend),
        };

        foreach ((string name, ulong expected) in documented)
        {
            Assert.IsTrue(
                declared.TryGetValue(name, out ulong actual),
                $"{name} is no longer declared.");
            Assert.AreEqual(
                expected,
                actual,
                $"{name} no longer matches the value winuser.h documents.");
        }
    }

    [TestMethod]
    public void EveryDeclaredConstant_IsOneThisContractPins()
    {
        // Reflection is what makes a newly added constant fail here until
        // somebody pins it to the header it came from. Without it, a constant
        // could be added, used, and quietly be wrong.
        string[] expected =
        [
            nameof(WindowsShellLifecycleMessages.DisplayChange),
            nameof(WindowsShellLifecycleMessages.PowerBroadcast),
            nameof(WindowsShellLifecycleMessages.ResumeAutomatic),
            nameof(WindowsShellLifecycleMessages.ResumeSuspend),
            nameof(WindowsShellLifecycleMessages.Suspend),
        ];

        string[] declared = typeof(WindowsShellLifecycleMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral)
            .Select(static field => field.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(
            expected,
            declared,
            "The set of pinned Windows message constants changed.");
    }

    [TestMethod]
    public void EveryConstant_HasTheWidthTheWindowProcedureReceivesItIn()
    {
        // A message id arrives as a UINT and a power notification arrives in the
        // WPARAM, which is pointer-sized. Declaring either the other way round
        // would compile and would then compare a sign-extended value against an
        // unsigned one on the boundary that matters.
        Dictionary<string, Type> expected = new(StringComparer.Ordinal)
        {
            [nameof(WindowsShellLifecycleMessages.DisplayChange)] = typeof(uint),
            [nameof(WindowsShellLifecycleMessages.PowerBroadcast)] = typeof(uint),
            [nameof(WindowsShellLifecycleMessages.ResumeAutomatic)] = typeof(nint),
            [nameof(WindowsShellLifecycleMessages.ResumeSuspend)] = typeof(nint),
            [nameof(WindowsShellLifecycleMessages.Suspend)] = typeof(nint),
        };

        foreach (FieldInfo field in typeof(WindowsShellLifecycleMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral))
        {
            Assert.IsTrue(
                expected.TryGetValue(field.Name, out Type? declaredType),
                $"{field.Name} is not pinned to a width.");
            Assert.AreEqual(
                declaredType,
                field.FieldType,
                $"{field.Name} changed width.");
        }
    }

    private static ulong Widen(uint value) => value;

    private static ulong Widen(nint value) => (ulong)value;
}
