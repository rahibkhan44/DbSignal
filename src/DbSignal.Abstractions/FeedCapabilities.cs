namespace DbSignal;

/// <summary>
/// What a feed can honestly do. Declared by every provider and verified by the
/// conformance suite — a provider that over-claims fails its tests.
/// </summary>
/// <param name="Detail">The most precise description of a change this feed can produce.</param>
/// <param name="DurableAcrossRestart">
/// Whether a <see cref="Checkpoint"/> persisted now still means something after the
/// process restarts. False for SQLite, whose <c>data_version</c> is a per-connection
/// counter with no meaning outside the connection that read it.
/// </param>
/// <param name="SurvivesDowntime">
/// Whether changes made while the feed was disconnected are still delivered on
/// reconnect. True for PostgreSQL (the replication slot retains WAL) and, within its
/// retention window, SQL Server. False for SQLite.
/// </param>
/// <param name="FiltersOwnWrites">
/// Whether the consuming application's own writes are excluded from the stream.
/// <para>
/// Read this as "will I hear about changes I made myself?", not as a statement about
/// connections. SQLite's <c>data_version</c> ignores commits on the connection that reads
/// it, but the feed's connection never writes — the application writes on its own — so
/// SQLite reports <see langword="false"/> here despite appearances. SQL Server Change
/// Tracking is <see langword="false"/> too. Where this is <see langword="false"/>,
/// handlers must tolerate seeing an echo of their own work.
/// </para>
/// </param>
/// <param name="RequiresProvisioning">
/// Whether the database needs one-time setup (DDL or server configuration) before this
/// feed will produce anything. Never done implicitly — see the provider's provisioner.
/// </param>
public sealed record FeedCapabilities(
    ChangeDetail Detail,
    bool DurableAcrossRestart,
    bool SurvivesDowntime,
    bool FiltersOwnWrites,
    bool RequiresProvisioning)
{
    /// <summary>
    /// Throws when this feed cannot meet the detail tier the caller requires. Called at
    /// startup so a mismatch is a launch failure with a clear message, never a silent
    /// six-month under-report.
    /// </summary>
    /// <param name="required">The minimum tier the consumer needs.</param>
    /// <param name="providerName">Provider name, used in the error message.</param>
    /// <exception cref="CapabilityNotSupportedException">The feed's tier is lower than <paramref name="required"/>.</exception>
    public void Require(ChangeDetail required, string providerName)
    {
        if (Detail < required)
        {
            throw new CapabilityNotSupportedException(
                $"{providerName} reports {nameof(ChangeDetail)}.{Detail}, but this application requires " +
                $"at least {nameof(ChangeDetail)}.{required}. " +
                "Either lower the requirement or use a provider that can supply it.");
        }
    }
}
