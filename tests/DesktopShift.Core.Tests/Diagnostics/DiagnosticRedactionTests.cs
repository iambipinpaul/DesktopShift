using DesktopShift.Core.Diagnostics;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class DiagnosticRedactionTests
{
    [TestMethod]
    public void UserProfilePath_IsReplacedWithTheToken()
    {
        string? redacted = DiagnosticRedaction.Redact(
            @"C:\Users\marguerite\AppData\Local\DesktopShift\logs");

        Assert.AreEqual(
            @"%USERPROFILE%\AppData\Local\DesktopShift\logs",
            redacted);
    }

    [TestMethod]
    public void ForwardSlashPaths_AreRedactedToo()
    {
        Assert.AreEqual(
            "%USERPROFILE%/Documents",
            DiagnosticRedaction.Redact("D:/Users/marguerite/Documents"));
    }

    [TestMethod]
    public void EveryOccurrence_IsRedacted()
    {
        string? redacted = DiagnosticRedaction.Redact(
            @"copied C:\Users\marguerite\a.txt to C:\Users\marguerite\b.txt");

        Assert.AreEqual(
            @"copied %USERPROFILE%\a.txt to %USERPROFILE%\b.txt",
            redacted);
    }

    [TestMethod]
    public void JsonEscapedPaths_AreRedactedToo()
    {
        // Log files are JSON, so a path reaches the redactor with its
        // backslashes already doubled.
        Assert.AreEqual(
            @"{""message"":""read %USERPROFILE%\\notes.txt""}",
            DiagnosticRedaction.Redact(
                @"{""message"":""read C:\\Users\\marguerite\\notes.txt""}"));
    }

    [TestMethod]
    public void SharedProfiles_AreLeftIntact()
    {
        Assert.AreEqual(
            @"C:\Users\Public\Desktop",
            DiagnosticRedaction.Redact(@"C:\Users\Public\Desktop"));
        Assert.AreEqual(
            @"C:\Users\Default\NTUSER.DAT",
            DiagnosticRedaction.Redact(@"C:\Users\Default\NTUSER.DAT"));
    }

    [TestMethod]
    public void NonProfilePaths_AreLeftIntact()
    {
        Assert.AreEqual(
            @"C:\Program Files\DesktopShift\DesktopShift.exe",
            DiagnosticRedaction.Redact(
                @"C:\Program Files\DesktopShift\DesktopShift.exe"));
    }

    [TestMethod]
    public void AnExplicitProfileDirectory_IsRedactedEvenWhenItIsRelocated()
    {
        string? redacted = DiagnosticRedaction.Redact(
            @"E:\Profiles\marguerite\AppData\Local\DesktopShift",
            @"E:\Profiles\marguerite\");

        Assert.AreEqual(
            @"%USERPROFILE%\AppData\Local\DesktopShift",
            redacted);
    }

    [TestMethod]
    public void RelocatedProfileMatch_IsCaseInsensitive()
    {
        Assert.AreEqual(
            @"%USERPROFILE%\logs",
            DiagnosticRedaction.Redact(
                @"E:\PROFILES\Marguerite\logs",
                @"e:\profiles\marguerite"));
    }

    [TestMethod]
    public void NullAndEmptyInput_AreReturnedUnchanged()
    {
        Assert.IsNull(DiagnosticRedaction.Redact(null));
        Assert.AreEqual(string.Empty, DiagnosticRedaction.Redact(string.Empty));
        Assert.IsNull(DiagnosticRedaction.Redact(null, @"C:\Users\marguerite"));
    }

    [TestMethod]
    public void AlreadyRedactedText_IsUnchanged()
    {
        Assert.AreEqual(
            @"%USERPROFILE%\logs",
            DiagnosticRedaction.Redact(@"%USERPROFILE%\logs"));
    }
}
