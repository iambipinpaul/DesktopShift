using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// A decoded application icon, carried as top-down 32-bit BGRA pixels.
/// </summary>
/// <remarks>
/// Raw pixels rather than an encoded image: the shell hands out an icon handle,
/// and every UI framework this application could draw with accepts a BGRA
/// buffer, so encoding and decoding a PNG in between would only add a dependency
/// and two conversions.
/// </remarks>
/// <param name="Width">The icon width in pixels.</param>
/// <param name="Height">The icon height in pixels.</param>
/// <param name="Pixels">
/// <c>Width * Height * 4</c> bytes, ordered blue, green, red, alpha, with the
/// first row at the start of the buffer.
/// </param>
public sealed record ApplicationIcon(
    int Width,
    int Height,
    ImmutableArray<byte> Pixels)
{
    /// <summary>
    /// Whether the icon carries the pixels its dimensions promise.
    /// </summary>
    public bool IsComplete =>
        Width > 0 &&
        Height > 0 &&
        !Pixels.IsDefault &&
        Pixels.Length == Width * Height * 4;
}

/// <summary>
/// Reads the icon an executable presents to the shell.
/// </summary>
/// <remarks>
/// The only new platform call the rule editor needs. It is an interface so the
/// selection logic can be tested without a desktop, a shell, or an installed
/// application.
/// </remarks>
public interface IApplicationIconReader
{
    /// <summary>
    /// Reads an executable's icon.
    /// </summary>
    /// <param name="executablePath">The full path of the executable.</param>
    /// <returns>
    /// The icon, or <see langword="null"/> when the path is unusable or the
    /// shell has no icon for it. A missing icon is never an error: an
    /// application without one is still a valid choice.
    /// </returns>
    ApplicationIcon? TryRead(string? executablePath);
}

/// <summary>
/// One running application offered for selection in the rule editor.
/// </summary>
/// <remarks>
/// A candidate is an application, not a window. Several windows of one
/// application collapse into one entry, because a rule names an application and
/// offering the same application once per open window would make the list
/// unreadable and the choice arbitrary.
/// <para>
/// No window title is carried. A title is the one window signal that routinely
/// contains a document name, an account, or a URL, and the picker has no need
/// for it: an application is identified by its process, package, path, and class.
/// </para>
/// </remarks>
/// <param name="ProcessName">The executable file name.</param>
/// <param name="ExecutablePath">The full image path, when it was readable.</param>
/// <param name="PackageFamilyName">
/// The packaged identity, when the application is packaged.
/// </param>
/// <param name="AppUserModelId">The shell identity, when the application has one.</param>
/// <param name="WindowClasses">
/// The distinct classes of the windows this application currently shows.
/// </param>
/// <param name="WindowCount">How many windows the application currently shows.</param>
/// <param name="Icon">The application's icon, when one could be read.</param>
public sealed record RunningApplicationCandidate(
    string ProcessName,
    string? ExecutablePath,
    string? PackageFamilyName,
    string? AppUserModelId,
    ImmutableArray<string> WindowClasses,
    int WindowCount,
    ApplicationIcon? Icon)
{
    /// <summary>
    /// The name shown in the picker: the executable name without its extension.
    /// </summary>
    public string DisplayName =>
        Path.GetFileNameWithoutExtension(ProcessName) is { Length: > 0 } name
            ? name
            : ProcessName;

    /// <summary>
    /// Whether the application declares a stable packaged identity, which the
    /// matcher ranks above a process name.
    /// </summary>
    public bool HasPackagedIdentity =>
        !string.IsNullOrWhiteSpace(PackageFamilyName) ||
        !string.IsNullOrWhiteSpace(AppUserModelId);

    /// <summary>
    /// The second line of a picker entry: how many windows the application has
    /// open, what it can be identified by, and where it is installed.
    /// </summary>
    /// <remarks>
    /// The identity strength is stated because it is the one thing a user cannot
    /// see for themselves and the one thing that decides whether the rule they
    /// are about to write is precise or a guess.
    /// </remarks>
    public string Summary
    {
        get
        {
            string windows = WindowCount == 1 ? "1 window" : $"{WindowCount} windows";
            string identity = !string.IsNullOrWhiteSpace(PackageFamilyName)
                ? $"Package: {PackageFamilyName}"
                : !string.IsNullOrWhiteSpace(AppUserModelId)
                    ? $"AppUserModelId: {AppUserModelId}"
                    : $"Process: {ProcessName}";
            return string.IsNullOrWhiteSpace(ExecutablePath)
                ? $"{windows} • {identity}"
                : $"{windows} • {identity} • {ExecutablePath}";
        }
    }
}

/// <summary>
/// The running windows as the rule editor sees them.
/// </summary>
/// <param name="Applications">
/// The distinct applications, ordered by name, for the picker.
/// </param>
/// <param name="Windows">
/// One identity per qualifying window, for evaluating a candidate rule against
/// what is actually open.
/// </param>
public sealed record RunningApplicationSnapshot(
    ImmutableArray<RunningApplicationCandidate> Applications,
    ImmutableArray<WindowIdentity> Windows)
{
    /// <summary>
    /// An empty snapshot, used before the first read completes.
    /// </summary>
    public static RunningApplicationSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// Lists the applications a rule could be written for.
/// </summary>
public interface IRunningApplicationInventory
{
    /// <summary>
    /// Reads the currently open windows.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The applications and the window identities behind them.</returns>
    ValueTask<RunningApplicationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the rule editor's view of the running windows out of the same
/// enumeration, qualification, and identity resolution the observer uses.
/// </summary>
/// <remarks>
/// Reusing those three collaborators is what makes the picker honest: an
/// application the classifier skips can never be assigned, so offering it would
/// be offering a rule that silently does nothing. Windows are only read, never
/// activated, moved, or attached to.
/// </remarks>
public sealed class RunningApplicationInventory : IRunningApplicationInventory
{
    private readonly ITopLevelWindowEnumerator windowEnumerator;
    private readonly IWindowClassifier classifier;
    private readonly IWindowIdentityResolver identityResolver;
    private readonly IApplicationIconReader iconReader;

    public RunningApplicationInventory(
        ITopLevelWindowEnumerator windowEnumerator,
        IWindowClassifier classifier,
        IWindowIdentityResolver identityResolver,
        IApplicationIconReader iconReader)
    {
        ArgumentNullException.ThrowIfNull(windowEnumerator);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(identityResolver);
        ArgumentNullException.ThrowIfNull(iconReader);

        this.windowEnumerator = windowEnumerator;
        this.classifier = classifier;
        this.identityResolver = identityResolver;
        this.iconReader = iconReader;
    }

    public async ValueTask<RunningApplicationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<WindowIdentity>.Builder windows =
            ImmutableArray.CreateBuilder<WindowIdentity>();
        Dictionary<string, CandidateAccumulator> accumulators =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (nint windowHandle in windowEnumerator.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();

            WindowQualification qualification = classifier.Qualify(windowHandle);
            if (qualification.Window is not QualifiedWindow window)
            {
                continue;
            }

            WindowIdentityResolution resolution = await identityResolver
                .ResolveAsync(window, cancellationToken)
                .ConfigureAwait(false);
            if (resolution.Identity is not WindowIdentity identity ||
                classifier.ClassifyIdentity(window, identity) !=
                    WindowSkipReason.None)
            {
                continue;
            }

            windows.Add(identity);
            string key = CreateKey(identity);
            if (!accumulators.TryGetValue(key, out CandidateAccumulator? accumulator))
            {
                accumulator = new CandidateAccumulator(identity);
                accumulators.Add(key, accumulator);
            }

            accumulator.Add(identity);
        }

        return new RunningApplicationSnapshot(
            [.. accumulators.Values
                .Select(accumulator => accumulator.ToCandidate(iconReader))
                .OrderBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.ExecutablePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)],
            windows.ToImmutable());
    }

    private static string CreateKey(WindowIdentity identity) =>
        string.Join(
            ' ',
            identity.ProcessName,
            identity.ExecutablePath ?? string.Empty,
            identity.PackageFamilyName ?? string.Empty,
            identity.AppUserModelId ?? string.Empty);

    private sealed class CandidateAccumulator
    {
        private readonly WindowIdentity first;
        private readonly SortedSet<string> windowClasses =
            new(StringComparer.OrdinalIgnoreCase);
        private int windowCount;

        public CandidateAccumulator(WindowIdentity first)
        {
            this.first = first;
        }

        public void Add(WindowIdentity identity)
        {
            windowCount++;
            if (!string.IsNullOrWhiteSpace(identity.WindowClass))
            {
                _ = windowClasses.Add(identity.WindowClass);
            }
        }

        public RunningApplicationCandidate ToCandidate(
            IApplicationIconReader iconReader) =>
            new(
                first.ProcessName,
                first.ExecutablePath,
                first.PackageFamilyName,
                first.AppUserModelId,
                [.. windowClasses],
                windowCount,
                iconReader.TryRead(first.ExecutablePath));
    }
}
