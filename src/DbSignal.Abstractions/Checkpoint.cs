namespace DbSignal;

/// <summary>
/// An opaque bookmark saying "I have seen everything up to here". Persist it, and a
/// restarted process resumes instead of replaying or skipping.
/// </summary>
/// <remarks>
/// The value is a provider-defined string — a SQL Server change-tracking version, a
/// PostgreSQL LSN, a MySQL binlog position. Do not parse it, compare it for ordering, or
/// hand a checkpoint from one provider to another. It is a token, not a number.
/// <para>
/// Only meaningful across restarts when the feed reports
/// <see cref="FeedCapabilities.DurableAcrossRestart"/>.
/// </para>
/// </remarks>
public readonly record struct Checkpoint
{
    /// <summary>Creates a checkpoint from a provider-issued token.</summary>
    /// <param name="value">The provider's opaque position token.</param>
    public Checkpoint(string value) => Value = value ?? string.Empty;

    /// <summary>The provider's opaque position token. Never empty except for <see cref="Beginning"/>.</summary>
    public string Value { get; }

    /// <summary>
    /// "Everything you still have." Providers that retain history replay from the oldest
    /// change they hold; providers that do not treat this as <see cref="Now"/>.
    /// </summary>
    public static Checkpoint Beginning => new(string.Empty);

    /// <summary>
    /// "Only what happens from here on." The right default for a cache or a screen, which
    /// cares about staying current rather than about history.
    /// </summary>
    public static Checkpoint Now => new("$now");

    /// <summary>True when this is <see cref="Beginning"/>.</summary>
    public bool IsBeginning => Value.Length == 0;

    /// <summary>True when this is <see cref="Now"/>.</summary>
    public bool IsNow => Value == "$now";

    /// <summary>Returns the underlying token, for logging.</summary>
    public override string ToString() => IsBeginning ? "(beginning)" : Value;
}
