namespace AeroDB;

using System.Text;

/// <summary>
/// Generates SurrealDB DDL for typed event stream tables.
/// Creates SCHEMAFULL tables with standard event stream fields and a unique
/// (stream_id, version) index for optimistic concurrency.
/// </summary>
internal static class EventStreamGenerator
{
    /// <summary>
    /// Builds the full SurrealQL to create an event stream table with fields
    /// and a unique stream-version index.
    /// </summary>
    /// <param name="config">The event stream configuration.</param>
    /// <returns>SurrealQL statements separated by newlines.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    public static string BuildCreateSurql(EventStreamConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sb = new StringBuilder();
        var table = config.TableName;

        if (string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException("EventStreamConfiguration.TableName is not set. Call UseTable<T>() first.");

        // Table definition (SCHEMAFULL for strict schema enforcement)
        sb.AppendLine($"DEFINE TABLE {table} SCHEMAFULL;");
        sb.AppendLine();

        // Standard event stream fields
        sb.AppendLine($"DEFINE FIELD stream_id ON TABLE {table} TYPE string;");
        sb.AppendLine($"DEFINE FIELD aggregate_id ON TABLE {table} TYPE option<record>;");
        sb.AppendLine($"DEFINE FIELD version ON TABLE {table} TYPE int;");
        sb.AppendLine($"DEFINE FIELD event_type ON TABLE {table} TYPE string;");
        sb.AppendLine($"DEFINE FIELD payload ON TABLE {table} TYPE object;");
        sb.AppendLine($"DEFINE FIELD metadata ON TABLE {table} TYPE object DEFAULT {{}};");
        sb.AppendLine($"DEFINE FIELD occurred_at ON TABLE {table} TYPE datetime DEFAULT time::now();");
        sb.AppendLine();

        // Unique constraint for optimistic concurrency (stream_id + version)
        sb.AppendLine($"DEFINE INDEX {table}_stream_version ON TABLE {table} FIELDS stream_id, version UNIQUE;");

        return sb.ToString();
    }
}
