namespace DbSignal.SqlServer;

/// <summary>A table to watch, and the SQL identifiers needed to reach it safely.</summary>
/// <param name="Schema">Schema name. Defaults to <c>dbo</c> when unqualified.</param>
/// <param name="Name">Table name.</param>
public sealed record WatchedTable(string Schema, string Name)
{
    /// <summary>
    /// Parses <c>Products</c> or <c>dbo.Products</c>. Bare names get the <c>dbo</c> schema,
    /// matching what a developer means when they type one.
    /// </summary>
    /// <param name="qualifiedName">Table name, optionally schema-qualified.</param>
    /// <exception cref="ArgumentException">The name is blank or has more than two parts.</exception>
    public static WatchedTable Parse(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            throw new ArgumentException("A table name is required.", nameof(qualifiedName));
        }

        var trimmed = qualifiedName.Trim().Replace("[", string.Empty, StringComparison.Ordinal)
                                          .Replace("]", string.Empty, StringComparison.Ordinal);
        var parts = trimmed.Split('.');

        return parts.Length switch
        {
            1 => new WatchedTable("dbo", parts[0]),
            2 => new WatchedTable(parts[0], parts[1]),
            _ => throw new ArgumentException(
                     $"'{qualifiedName}' is not a table name. Expected 'Table' or 'Schema.Table'.",
                     nameof(qualifiedName)),
        };
    }

    /// <summary>
    /// The bracket-quoted identifier for use in SQL. Embedded <c>]</c> is doubled, which is
    /// what makes this safe to interpolate — table names cannot be parameters in
    /// <c>CHANGETABLE</c>, so quoting is the only defence available.
    /// </summary>
    public string QuotedName => $"[{Escape(Schema)}].[{Escape(Name)}]";

    /// <summary>The plain two-part name, for <c>OBJECT_ID</c> lookups and messages.</summary>
    public string QualifiedName => $"{Schema}.{Name}";

    private static string Escape(string identifier) =>
        identifier.Replace("]", "]]", StringComparison.Ordinal);
}
