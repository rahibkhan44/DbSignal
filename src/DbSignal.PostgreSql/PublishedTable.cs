namespace DbSignal.PostgreSql;

/// <summary>A table to watch, and the SQL identifiers needed to reach it safely.</summary>
/// <param name="Schema">Schema name. Defaults to <c>public</c> when unqualified.</param>
/// <param name="Name">Table name.</param>
/// <remarks>
/// Deliberately not shared with the SQL Server provider's <c>WatchedTable</c>. Three rules
/// differ and none can be parameterised away: the default schema is <c>public</c> rather
/// than <c>dbo</c>; quoting is <c>"…"</c> with embedded <c>"</c> doubled, not <c>[…]</c>;
/// and PostgreSQL <strong>folds unquoted identifiers to lower case</strong>, so
/// <c>Products</c> written in a connection string resolves to <c>products</c> on the server.
/// Getting that last one wrong means watching a table that does not exist, which produces
/// no error — just a feed that never reports anything.
/// </remarks>
public sealed record PublishedTable(string Schema, string Name)
{
    /// <summary>
    /// Parses <c>products</c> or <c>public.products</c>. Bare names get the <c>public</c>
    /// schema. An unquoted name is lower-cased to match how the server will resolve it; a
    /// name already wrapped in double quotes keeps its case.
    /// </summary>
    /// <param name="qualifiedName">Table name, optionally schema-qualified.</param>
    /// <exception cref="ArgumentException">The name is blank or has more than two parts.</exception>
    public static PublishedTable Parse(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            throw new ArgumentException("A table name is required.", nameof(qualifiedName));
        }

        var parts = SplitRespectingQuotes(qualifiedName.Trim());

        return parts.Count switch
        {
            1 => new PublishedTable("public", Fold(parts[0])),
            2 => new PublishedTable(Fold(parts[0]), Fold(parts[1])),
            _ => throw new ArgumentException(
                     $"'{qualifiedName}' is not a table name. Expected 'table' or 'schema.table'.",
                     nameof(qualifiedName)),
        };
    }

    /// <summary>
    /// The double-quoted identifier for use in SQL. Embedded <c>"</c> is doubled, which is
    /// what makes this safe to interpolate — a table name cannot be a parameter in
    /// <c>CREATE PUBLICATION</c> or <c>ALTER TABLE</c>, so quoting is the only defence.
    /// </summary>
    public string QuotedName => $"\"{Escape(Schema)}\".\"{Escape(Name)}\"";

    /// <summary>The plain two-part name, for catalogue lookups and messages.</summary>
    public string QualifiedName => $"{Schema}.{Name}";

    private static string Escape(string identifier) =>
        identifier.Replace("\"", "\"\"", StringComparison.Ordinal);

    /// <summary>
    /// Lower-cases an unquoted identifier the way the server would, and unwraps a quoted one
    /// while preserving its case.
    /// </summary>
    private static string Fold(string part)
    {
        var trimmed = part.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Splits on <c>.</c> but not inside double quotes, so <c>"my.schema".products</c> is two
    /// parts rather than three.
    /// </summary>
    private static List<string> SplitRespectingQuotes(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    _ = current.Append(c);
                    break;

                case '.' when !inQuotes:
                    parts.Add(current.ToString());
                    _ = current.Clear();
                    break;

                default:
                    _ = current.Append(c);
                    break;
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
