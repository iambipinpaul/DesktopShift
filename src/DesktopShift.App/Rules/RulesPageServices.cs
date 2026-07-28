using System;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Rules;

/// <summary>
/// Everything the Rules page needs to read, edit, and prove Application Rules.
/// </summary>
/// <remarks>
/// Bundled into one record so the shell hands the page a single value. The page
/// is navigated to and refreshed repeatedly, and a long parameter list repeated
/// at every refresh is where a wiring mistake hides.
/// </remarks>
/// <param name="ConfigurationService">
/// Reads the candidate document and persists every edit. The candidate is what
/// the page shows: a document rejected by validation is still the document the
/// user is working on, and it must not disappear because it is invalid.
/// </param>
/// <param name="ActivityProjection">
/// Supplies the last time each rule claimed a window. It carries only
/// privacy-safe identities.
/// </param>
/// <param name="RunningApplications">
/// Lists the applications a rule can be written for, and the window identities a
/// rule is tested against.
/// </param>
/// <param name="IconReader">Reads the icon of a rule's executable.</param>
/// <param name="WindowReassignmentService">
/// Runs the manual reassignment batch behind "reassign matching windows now".
/// </param>
/// <param name="TimeProvider">
/// Supplies the moment the page renders, which is what makes the last-match
/// times relative.
/// </param>
public sealed record RulesPageServices(
    IConfigurationService ConfigurationService,
    IWindowObservationActivityProjection ActivityProjection,
    IRunningApplicationInventory RunningApplications,
    IApplicationIconReader IconReader,
    IWindowReassignmentService WindowReassignmentService,
    TimeProvider TimeProvider);
