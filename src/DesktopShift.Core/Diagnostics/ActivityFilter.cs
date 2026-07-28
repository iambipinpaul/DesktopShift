using System.Collections.Immutable;

namespace DesktopShift.Core.Diagnostics;

/// <summary>
/// The bounded set of time windows the Activity view offers.
/// </summary>
/// <remarks>
/// A fixed vocabulary rather than a free date pair: every option resolves
/// deterministically from a supplied "now", which is what makes the filter
/// testable without a clock.
/// </remarks>
public enum ActivityDateFilter
{
    AllTime,
    CurrentSession,
    LastHour,
    Today,
    LastSevenDays,
}

/// <summary>
/// A pure predicate over <see cref="ActivityRecord"/>.
/// </summary>
/// <remarks>
/// Every filter the Activity view offers — result, application, rule, and date
/// or session — lives here so it can be proved exhaustively without a UI.
/// </remarks>
public sealed record ActivityFilter
{
    /// <summary>A filter that admits every record.</summary>
    public static ActivityFilter None { get; } = new();

    /// <summary>
    /// The results to admit. An empty or default array admits every result.
    /// </summary>
    public ImmutableArray<ActivityResult> Results { get; init; } = [];

    /// <summary>
    /// The application to admit, compared case-insensitively against the
    /// record's process name. Null or whitespace admits every application.
    /// </summary>
    public string? Application { get; init; }

    /// <summary>
    /// The rule identifier to admit, compared case-insensitively. Null or
    /// whitespace admits every rule, including records with no rule.
    /// </summary>
    public string? RuleId { get; init; }

    /// <summary>The time window to admit.</summary>
    public ActivityDateFilter Date { get; init; } = ActivityDateFilter.AllTime;

    /// <summary>
    /// The application run <see cref="ActivityDateFilter.CurrentSession"/>
    /// resolves against. Null makes that option admit nothing, which is honest:
    /// without a session there is no current session to show.
    /// </summary>
    public Guid? SessionId { get; init; }

    /// <summary>Decides whether one record survives the filter.</summary>
    /// <param name="record">The record to test.</param>
    /// <param name="now">The instant the date window is measured from.</param>
    /// <returns><c>true</c> when the record is admitted.</returns>
    public bool Matches(ActivityRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A default ImmutableArray cannot be enumerated, so it is normalized to
        // "no restriction" before it is read.
        ImmutableArray<ActivityResult> results =
            Results.IsDefault ? [] : Results;
        if (results.Length > 0 && !results.Contains(record.Result))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Application) &&
            !string.Equals(
                record.Application,
                Application.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(RuleId) &&
            !string.Equals(
                record.RuleId,
                RuleId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesDate(record, now);
    }

    /// <summary>Applies the filter, preserving the source order.</summary>
    /// <param name="records">The records to filter.</param>
    /// <param name="now">The instant the date window is measured from.</param>
    /// <returns>The admitted records.</returns>
    public IReadOnlyList<ActivityRecord> Apply(
        IEnumerable<ActivityRecord> records,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Where(record => Matches(record, now)).ToArray();
    }

    private bool MatchesDate(ActivityRecord record, DateTimeOffset now) =>
        Date switch
        {
            ActivityDateFilter.AllTime => true,
            ActivityDateFilter.CurrentSession =>
                SessionId is Guid sessionId && record.SessionId == sessionId,
            ActivityDateFilter.LastHour =>
                record.OccurredAt > now - TimeSpan.FromHours(1),
            ActivityDateFilter.Today =>
                record.OccurredAt.ToLocalTime().Date == now.ToLocalTime().Date,
            ActivityDateFilter.LastSevenDays =>
                record.OccurredAt > now - TimeSpan.FromDays(7),
            _ => true,
        };
}

/// <summary>
/// The distinct values present in a set of records, so the Activity view can
/// offer only filters that would actually select something.
/// </summary>
public sealed record ActivityFilterOptions(
    ImmutableArray<string> Applications,
    ImmutableArray<string> RuleIds)
{
    public static ActivityFilterOptions Empty { get; } = new([], []);

    public static ActivityFilterOptions From(IEnumerable<ActivityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        SortedSet<string> applications = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> ruleIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (ActivityRecord record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.Application))
            {
                _ = applications.Add(record.Application.Trim());
            }

            if (!string.IsNullOrWhiteSpace(record.RuleId))
            {
                _ = ruleIds.Add(record.RuleId.Trim());
            }
        }

        return new ActivityFilterOptions(
            [.. applications],
            [.. ruleIds]);
    }
}
