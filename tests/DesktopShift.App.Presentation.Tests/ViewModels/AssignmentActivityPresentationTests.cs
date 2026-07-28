using System.Collections.Immutable;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Presentation.Tests.ViewModels;

[TestClass]
public sealed class AssignmentActivityPresentationTests
{
    [TestMethod]
    public void Create_SuccessShowsCorrelationDurationAndPrivacySafeIdentity()
    {
        Guid correlationId = Guid.Parse("16118a26-8f89-48dc-8b55-826f85735e44");
        WindowAssignmentActivity activity = CreateActivity(
            correlationId,
            WindowAssignmentOutcome.Succeeded,
            WindowAssignmentSkipReason.None,
            TimeSpan.FromMilliseconds(12.4));

        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(activity);

        Assert.AreEqual("Assigned", presentation.Outcome);
        Assert.AreEqual("Assigned Code.exe to code", presentation.Decision);
        Assert.AreEqual("12.4 ms", presentation.Duration);
        Assert.Contains(correlationId.ToString("D"), presentation.Correlation);
        Assert.Contains("Window class: Chrome_WidgetWin_1", presentation.IdentityDetails);
        Assert.DoesNotContain("window title", presentation.AutomationName);
        Assert.DoesNotContain("command line", presentation.AutomationName);
    }

    [TestMethod]
    public void Create_AlreadyCorrectIsAnExplicitSkippedOutcome()
    {
        WindowAssignmentActivity activity = CreateActivity(
            Guid.NewGuid(),
            WindowAssignmentOutcome.Skipped,
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            TimeSpan.FromTicks(1));

        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(activity);

        Assert.AreEqual("Already correct", presentation.Outcome);
        Assert.AreEqual("Code.exe is already on code", presentation.Decision);
        Assert.AreEqual("<1 ms", presentation.Duration);
        Assert.AreEqual("No move was needed.", presentation.Diagnostic);
    }

    [TestMethod]
    public void Create_FailureShowsStructuredErrorWithoutPrivateWindowContent()
    {
        WindowAssignmentActivity source = CreateActivity(
            Guid.NewGuid(),
            WindowAssignmentOutcome.Failed,
            WindowAssignmentSkipReason.None,
            TimeSpan.FromSeconds(1.25));
        WindowAssignmentActivity activity = source with
        {
            Error = new WindowAssignmentError(
                "assignment.move_failed",
                "Windows rejected the move.",
                HResult: unchecked((int)0x80004005),
                NativeErrorCode: 5),
        };

        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(activity);

        Assert.AreEqual("Failed", presentation.Outcome);
        Assert.AreEqual("1.25 s", presentation.Duration);
        Assert.Contains("assignment.move_failed", presentation.Diagnostic);
        Assert.Contains("HRESULT 0x80004005", presentation.Diagnostic);
        Assert.Contains("Windows error 5", presentation.Diagnostic);
    }

    [TestMethod]
    public async Task ReassignmentCommandCallsServiceOnceAndAwaitsStructuredCompletion()
    {
        TaskCompletionSource<WindowReassignmentBatchResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeReassignmentService service = new(completion.Task);
        WindowReassignmentCommand command = new(service);

        Task<ReassignmentBatchPresentation> pending = command.ExecuteAsync();

        Assert.AreEqual(1, service.ReassignAllCallCount);
        Assert.IsTrue(command.IsExecuting);

        bool rejectedOverlap = false;
        try
        {
            _ = await command.ExecuteAsync();
        }
        catch (InvalidOperationException)
        {
            rejectedOverlap = true;
        }

        Assert.IsTrue(rejectedOverlap);
        Assert.AreEqual(1, service.ReassignAllCallCount);

        WindowAssignmentActivity assignment = CreateActivity(
            Guid.NewGuid(),
            WindowAssignmentOutcome.Succeeded,
            WindowAssignmentSkipReason.None,
            TimeSpan.FromMilliseconds(4));
        WindowReassignmentBatchResult result = new(
            Guid.Parse("955b0e0d-9761-46b6-af75-2641d69411c0"),
            assignment.StartedAtUtc,
            TimeSpan.FromMilliseconds(8),
            WindowEventKind.ManualReassignment,
            EnumeratedWindowCount: 1,
            ImmutableArray.Create(assignment));
        completion.SetResult(result);

        ReassignmentBatchPresentation presentation = await pending;

        Assert.IsFalse(command.IsExecuting);
        Assert.AreEqual("Reassignment completed", presentation.Title);
        Assert.Contains("1 assigned, 0 skipped, 0 failed", presentation.Message);
        Assert.Contains(result.CorrelationId.ToString("D"), presentation.Message);
    }

    private static WindowAssignmentActivity CreateActivity(
        Guid correlationId,
        WindowAssignmentOutcome outcome,
        WindowAssignmentSkipReason skipReason,
        TimeSpan duration) =>
        new(
            correlationId,
            new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero),
            duration,
            WindowEventKind.ManualReassignment,
            WindowHandle: (nint)42,
            outcome,
            skipReason,
            RuleId: "vscode",
            TargetDesktopKey: "code",
            TargetDesktopId: Guid.Parse("0527a4d4-18cf-470d-9abf-3672fce6fcd0"),
            PreviousDesktopId: Guid.Parse("d13b236c-c9fb-41e8-85a5-1bff1ffbdf9f"),
            new WindowSafeIdentity(
                "Code.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                WindowClass: "Chrome_WidgetWin_1"),
            Error: null);

    private sealed class FakeReassignmentService(
        Task<WindowReassignmentBatchResult> result) :
        IWindowReassignmentService
    {
        public int ReassignAllCallCount { get; private set; }

        public Task<WindowReassignmentBatchResult> ReconcileStartupAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WindowReassignmentBatchResult> ReassignAllAsync(
            CancellationToken cancellationToken = default)
        {
            ReassignAllCallCount++;
            return result;
        }
    }
}
