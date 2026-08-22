using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// One automatic-tiling rule naming the applications whose windows stay out of
/// the layout.
/// </summary>
/// <remarks>
/// <para>
/// The match fields are exactly the identity signals an Application Rule uses —
/// process names, package family names, AppUserModelIds, executable paths, and
/// window classes — resolved by the same identity resolver. A window title and
/// a command line are deliberately absent for the reason
/// <see cref="ApplicationRule"/> leaves them out: the resolver collects them
/// only when explicitly opted into, so a rule built on them could never match.
/// </para>
/// <para>
/// A rule carries no destination and no triggers because it moves nothing. It
/// answers one question — does this application take part in the layout — and
/// nothing else about a rule's shape would change that answer.
/// </para>
/// </remarks>
/// <param name="Id">The stable rule identifier.</param>
/// <param name="DisplayName">The rule name shown in the UI.</param>
/// <param name="IsEnabled">Whether the rule takes part in matching.</param>
/// <param name="ProcessNames">
/// Executable file names. The weakest identity, and never proof that the
/// named process owns the window.
/// </param>
/// <param name="PackageFamilyNames">
/// Optional packaged application identities. The strongest signal.
/// </param>
/// <param name="AppUserModelIds">
/// Optional shell application identities.
/// </param>
/// <param name="ExecutablePaths">
/// Optional full executable paths. Stronger than a process name because a path
/// distinguishes two installations that share a file name.
/// </param>
/// <param name="WindowClasses">
/// Optional exact window class refinement. A rule that declares one matches
/// only windows carrying that class.
/// </param>
public sealed record TilingIdentityRule(
    string Id,
    string DisplayName,
    bool IsEnabled,
    ImmutableArray<string> ProcessNames = default,
    ImmutableArray<string> PackageFamilyNames = default,
    ImmutableArray<string> AppUserModelIds = default,
    ImmutableArray<string> ExecutablePaths = default,
    ImmutableArray<string> WindowClasses = default)
{
    /// <summary>
    /// Executable file names, normalized away from a default array.
    /// </summary>
    public ImmutableArray<string> ProcessNames { get; init; } =
        ProcessNames.IsDefault ? [] : ProcessNames;

    /// <summary>
    /// Packaged application identities, normalized so an omitted collection is
    /// empty rather than a default array, exactly as
    /// <see cref="ApplicationRule"/> normalizes its own.
    /// </summary>
    public ImmutableArray<string> PackageFamilyNames { get; init; } =
        PackageFamilyNames.IsDefault ? [] : PackageFamilyNames;

    /// <summary>Shell application identities, normalized the same way.</summary>
    public ImmutableArray<string> AppUserModelIds { get; init; } =
        AppUserModelIds.IsDefault ? [] : AppUserModelIds;

    /// <summary>Full executable paths, normalized the same way.</summary>
    public ImmutableArray<string> ExecutablePaths { get; init; } =
        ExecutablePaths.IsDefault ? [] : ExecutablePaths;

    /// <summary>Exact window classes, normalized the same way.</summary>
    public ImmutableArray<string> WindowClasses { get; init; } =
        WindowClasses.IsDefault ? [] : WindowClasses;
}

/// <summary>The layout engine used by automatic tiling.</summary>
public enum TilingLayout
{
    /// <summary>Binary space partitioning.</summary>
    Bsp,
}

/// <summary>How a new tiled window chooses the leaf to split.</summary>
public enum TilingInsertMode
{
    /// <summary>Split the focused leaf when possible, else the largest leaf.</summary>
    SplitFocusedOrLargest,
}

/// <summary>
/// Everything the automatic window layout reads from the configuration
/// document.
/// </summary>
/// <remarks>
/// <para>
/// A trailing member of <see cref="ConfigurationDocument"/> carrying the value
/// a document written before tiling existed should be read as — switched off —
/// which is why the schema version did not have to move. Every collection
/// trails the scalars and normalizes away from a default array, following the
/// pattern <see cref="ApplicationRule"/> established.
/// </para>
/// <para>
/// Gaps and minimum tile sizes are DPI-scaled units: values expressed against
/// a 96-DPI reference and multiplied by each monitor's scale factor at plan
/// time. Physical pixels would tile a 150% display with gaps a third too small
/// and minimum tiles a third too tight, and would make both settings mean a
/// different physical size on every monitor a user owns.
/// </para>
/// <para>
/// <see cref="ApplyToAllVirtualDesktops"/> keeps the common layout parameters
/// shared by all desktops. <see cref="DisabledManagedDesktopKeys"/> supplies
/// the per-desktop on/off choice. It uses stable semantic keys instead of the
/// Windows desktop IDs that can change after shell recovery.
/// </para>
/// </remarks>
/// <param name="IsEnabled">
/// Whether DesktopShift lays out windows at all. Off by default, so upgrading
/// changes nothing until the user asks for it; a disabled layout performs no
/// window placement whatsoever.
/// </param>
/// <param name="Layout">The layout engine. The first release supports BSP.</param>
/// <param name="OuterGap">
/// Space between the monitor work-area edge and its tiles.
/// </param>
/// <param name="InnerGap">
/// Space between neighbouring tiles, in DPI-scaled units.
/// </param>
/// <param name="InsertMode">
/// How a new window selects the existing tile that it splits.
/// </param>
/// <param name="MinimumTileWidth">
/// The narrowest tile the planner will create by splitting, in DPI-scaled
/// units.
/// </param>
/// <param name="MinimumTileHeight">
/// The shortest tile the planner will create by splitting, in DPI-scaled
/// units.
/// </param>
/// <param name="ApplyToAllVirtualDesktops">
/// Whether virtual desktops share the same gaps, layout, insert mode, and
/// minimum tile size. Only <see langword="true"/> is supported. Individual
/// managed desktops can still turn the shared policy on or off.
/// </param>
/// <param name="FloatRules">
/// Applications whose windows are kept out of the tree but still observed —
/// moved between desktops by assignment, never resized or repositioned by the
/// layout.
/// </param>
/// <param name="IgnoreRules">
/// Applications whose windows the layout does not touch at all and does not
/// track: not tiled, not floated, not reported. Shell surfaces and windows
/// owned by elevated processes land here naturally; a user adds rules for
/// anything else they want DesktopShift to leave completely alone.
/// </param>
/// <param name="DisabledManagedDesktopKeys">
/// Stable semantic keys for managed desktops where automatic tiling is off.
/// An empty collection keeps the earlier behavior: all desktops are tiled.
/// </param>
/// <remarks>
/// The scalar defaults are written as literals because a parameter default
/// cannot name this type's own constants. They mirror
/// <see cref="DefaultOuterGap"/>, <see cref="DefaultInnerGap"/>,
/// <see cref="DefaultMinimumTileWidth"/>, and
/// <see cref="DefaultMinimumTileHeight"/>.
/// </remarks>
public sealed record TilingSettings(
    bool IsEnabled = false,
    TilingLayout Layout = TilingLayout.Bsp,
    int OuterGap = 5,
    int InnerGap = 10,
    TilingInsertMode InsertMode = TilingInsertMode.SplitFocusedOrLargest,
    int MinimumTileWidth = 320,
    int MinimumTileHeight = 240,
    bool ApplyToAllVirtualDesktops = true,
    ImmutableArray<TilingIdentityRule> FloatRules = default,
    ImmutableArray<TilingIdentityRule> IgnoreRules = default,
    ImmutableArray<string> DisabledManagedDesktopKeys = default)
{
    /// <summary>The gap a new document ships with.</summary>
    public const int DefaultOuterGap = 5;

    /// <summary>The gap between tiles in a new document.</summary>
    public const int DefaultInnerGap = 10;

    /// <summary>The narrowest splittable width a new document ships with.</summary>
    public const int DefaultMinimumTileWidth = 320;

    /// <summary>The shortest splittable height a new document ships with.</summary>
    public const int DefaultMinimumTileHeight = 240;

    /// <summary>The smallest gap that still reads as a gap on a 100% display.</summary>
    public const int MinimumGap = 0;

    /// <summary>
    /// The largest gap allowed. Beyond this the gaps stop being separators and
    /// start being the layout, which is not what the setting is for.
    /// </summary>
    public const int MaximumGap = 256;

    /// <summary>
    /// The smallest tile dimension accepted. Below this a tile cannot show a
    /// caption bar and its own content at once on any current display.
    /// </summary>
    public const int AbsoluteMinimumTileDimension = 64;

    /// <summary>The largest tile dimension accepted.</summary>
    public const int MaximumTileDimension = 4096;

    /// <summary>The settings a document written before tiling existed reads as.</summary>
    public static TilingSettings Disabled { get; } = new();

    /// <summary>Float rules, normalized away from a default array.</summary>
    public ImmutableArray<TilingIdentityRule> FloatRules { get; init; } =
        FloatRules.IsDefault ? [] : FloatRules;

    /// <summary>Ignore rules, normalized away from a default array.</summary>
    public ImmutableArray<TilingIdentityRule> IgnoreRules { get; init; } =
        IgnoreRules.IsDefault ? [] : IgnoreRules;

    /// <summary>
    /// Managed desktop semantic keys where automatic tiling is off.
    /// </summary>
    public ImmutableArray<string> DisabledManagedDesktopKeys { get; init; } =
        DisabledManagedDesktopKeys.IsDefault ? [] : DisabledManagedDesktopKeys;

    /// <summary>Returns whether a managed desktop takes part in tiling.</summary>
    public bool IsEnabledForManagedDesktop(string semanticKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);
        return !DisabledManagedDesktopKeys.Contains(
            semanticKey,
            StringComparer.OrdinalIgnoreCase);
    }
}
