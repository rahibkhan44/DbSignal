namespace DbSignal;

/// <summary>
/// How precisely a feed can describe a change. Ordered least to most detailed, so
/// <c>&gt;=</c> comparisons are meaningful — <c>Detail &gt;= ChangeDetail.KeysChanged</c>
/// reads as "can tell me which rows".
/// </summary>
/// <remarks>
/// <para>
/// Databases genuinely differ here, and this enum exists so the library never pretends
/// otherwise. A consumer states the tier it needs via
/// <c>RequireAtLeast</c> and gets a startup failure on a provider that cannot deliver,
/// rather than silently under-reporting for months.
/// </para>
/// <list type="table">
///   <listheader><term>Tier</term><description>Engines</description></listheader>
///   <item>
///     <term><see cref="DatabaseChanged"/></term>
///     <description>SQLite (<c>PRAGMA data_version</c>) — one number for the whole file.</description>
///   </item>
///   <item>
///     <term><see cref="TableChanged"/></term>
///     <description>Any polling scheme that can attribute a change to a table.</description>
///   </item>
///   <item>
///     <term><see cref="KeysChanged"/></term>
///     <description>SQL Server Change Tracking — names the changed primary keys, not the values.</description>
///   </item>
///   <item>
///     <term><see cref="RowImages"/></term>
///     <description>PostgreSQL logical replication, MySQL binlog, SQL Server CDC — full before/after rows.</description>
///   </item>
/// </list>
/// </remarks>
public enum ChangeDetail
{
    /// <summary>
    /// "Something in this database changed." No table, no rows. Enough to invalidate a
    /// cache or reload a screen; not enough to patch a single row in place.
    /// </summary>
    DatabaseChanged = 0,

    /// <summary>"These tables changed." Enough to reload only the affected lists.</summary>
    TableChanged = 1,

    /// <summary>
    /// "These rows changed", identified by primary key. Enough to re-read just those rows.
    /// The values themselves still require a query.
    /// </summary>
    KeysChanged = 2,

    /// <summary>
    /// "Here is the row before and after." No follow-up query needed, and the only tier
    /// that can reconstruct deletes with their old values.
    /// </summary>
    RowImages = 3,
}
