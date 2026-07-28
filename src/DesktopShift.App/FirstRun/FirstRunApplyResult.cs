namespace DesktopShift.App.FirstRun;

public sealed record FirstRunApplyResult(bool Accepted, IReadOnlyList<string> ValidationMessages);
