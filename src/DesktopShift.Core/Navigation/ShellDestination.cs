namespace DesktopShift.Core.Navigation;

/// <summary>
/// A platform-neutral destination in the primary application shell.
/// </summary>
/// <param name="Key">A stable key used for navigation and selection.</param>
/// <param name="Label">The user-facing destination label.</param>
/// <param name="Order">The destination's zero-based display order.</param>
public sealed record ShellDestination(string Key, string Label, int Order);
