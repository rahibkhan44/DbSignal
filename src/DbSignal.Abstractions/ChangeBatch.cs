namespace DbSignal;

/// <summary>What happened to a row.</summary>
public enum ChangeKind
{
    /// <summary>The provider cannot say which of the three it was.</summary>
    Unknown = 0,

    /// <summary>The row was created.</summary>
    Insert = 1,

    /// <summary>The row already existed and was modified.</summary>
    Update = 2,

    /// <summary>The row was removed.</summary>
    Delete = 3,
}

/// <summary>
/// The primary key of a changed row, plus what happened to it. Available from
/// <see cref="ChangeDetail.KeysChanged"/> upward.
/// </summary>
/// <param name="Values">
/// Key column values in the table's key order — one entry for a simple key, several for
/// a composite one.
/// </param>
/// <param name="Kind">What happened to the row, where the provider can tell.</param>
public sealed record ChangeKey(IReadOnlyList<object?> Values, ChangeKind Kind)
{
    /// <summary>Convenience for the common single-column key.</summary>
    /// <param name="value">The key value.</param>
    /// <param name="kind">What happened to the row.</param>
    public static ChangeKey FromValue(object? value, ChangeKind kind) => new(new[] { value }, kind);

    /// <summary>Renders the key for logging, e.g. <c>Update(42)</c>.</summary>
    public override string ToString() => $"{Kind}({string.Join(", ", Values)})";
}

/// <summary>
/// A row's contents before and/or after the change. Only produced at
/// <see cref="ChangeDetail.RowImages"/>.
/// </summary>
/// <param name="Kind">What happened to the row.</param>
/// <param name="Before">Column values before the change. Null for an insert.</param>
/// <param name="After">Column values after the change. Null for a delete.</param>
public sealed record RowImage(
    ChangeKind Kind,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After);

/// <summary>Everything that happened to one table within a batch.</summary>
/// <param name="Schema">Schema name — <c>dbo</c>, <c>public</c>, or empty where the engine has none.</param>
/// <param name="Name">Table name, unqualified.</param>
/// <param name="Keys">Changed row keys. Empty below <see cref="ChangeDetail.KeysChanged"/>.</param>
/// <param name="Rows">Before/after images. Empty below <see cref="ChangeDetail.RowImages"/>.</param>
public sealed record TableChange(
    string Schema,
    string Name,
    IReadOnlyList<ChangeKey> Keys,
    IReadOnlyList<RowImage> Rows)
{
    /// <summary>Creates a table-level change carrying no row detail.</summary>
    /// <param name="schema">Schema name, or empty.</param>
    /// <param name="name">Table name.</param>
    public static TableChange TableOnly(string schema, string name) =>
        new(schema, name, Array.Empty<ChangeKey>(), Array.Empty<RowImage>());

    /// <summary>The schema-qualified name, or just the name when there is no schema.</summary>
    public string QualifiedName => Schema.Length == 0 ? Name : $"{Schema}.{Name}";

    /// <summary>Renders the change for logging.</summary>
    public override string ToString() => Keys.Count == 0
        ? QualifiedName
        : $"{QualifiedName} ({Keys.Count} rows)";
}

/// <summary>
/// One set of changes observed together, and the position that follows them.
/// </summary>
/// <param name="Position">
/// Persist this <em>after</em> the batch is handled. Doing it before turns a handler
/// crash into silent data loss.
/// </param>
/// <param name="Tables">
/// The affected tables. <strong>Empty at <see cref="ChangeDetail.DatabaseChanged"/></strong>,
/// where the batch means only "something changed" — check
/// <see cref="IChangeFeed.Capabilities"/> before assuming otherwise.
/// </param>
/// <param name="ObservedUtc">When the feed noticed, not when the database committed.</param>
public sealed record ChangeBatch(
    Checkpoint Position,
    IReadOnlyList<TableChange> Tables,
    DateTimeOffset ObservedUtc)
{
    /// <summary>Renders the batch for logging.</summary>
    public override string ToString() => Tables.Count == 0
        ? $"database changed @ {Position}"
        : $"{string.Join(", ", Tables)} @ {Position}";
}
