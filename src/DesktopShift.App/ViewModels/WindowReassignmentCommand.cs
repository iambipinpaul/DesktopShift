using DesktopShift.Core.Assignments;

namespace DesktopShift.App.ViewModels;

public sealed class WindowReassignmentCommand
{
    private readonly IWindowReassignmentService service;
    private int isExecuting;

    public WindowReassignmentCommand(IWindowReassignmentService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public bool IsExecuting => Volatile.Read(ref isExecuting) != 0;

    public async Task<ReassignmentBatchPresentation> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref isExecuting, 1) != 0)
        {
            throw new InvalidOperationException(
                "A manual reassignment batch is already running.");
        }

        try
        {
            WindowReassignmentBatchResult result =
                await service.ReassignAllAsync(cancellationToken);
            return ReassignmentBatchPresentation.Create(result);
        }
        finally
        {
            _ = Interlocked.Exchange(ref isExecuting, 0);
        }
    }
}
