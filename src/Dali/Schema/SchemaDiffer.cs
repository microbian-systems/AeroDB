using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;
using Dali.Metadata;
using SurrealDb.Net;

namespace Dali;

/// <summary>
/// Computes schema differences between configured mappings and the live database.
/// Access via <c>store.Advanced.ComputeSchemaDiffAsync()</c>.
/// </summary>
public class SchemaDiffer
{
    private readonly ISurrealDbClient _client;
    private readonly StoreOptions _options;

    public SchemaDiffer(ISurrealDbClient client, StoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Compute the schema diff. Compares configured tables against the live database.</summary>
    public async Task<SchemaDiff> ComputeDiffAsync(CancellationToken ct = default)
    {
        var diff = new SchemaDiff();

        // Get configured table names from registered mappings
        var configuredTables = _options.Schema.Mappings.Values
            .Select(m => MetadataDispatch.GetTableName(m.DocumentType))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configuredTables.Count == 0)
            return diff; // No configured tables to compare

        // For each configured table, query INFO FOR TABLE to get actual schema
        foreach (var tableName in configuredTables)
        {
            try
            {
                var infoResponse = await _client.RawQuery($"INFO FOR TABLE `{tableName}`;", null, ct).ConfigureAwait(false);

                if (infoResponse.HasErrors)
                {
                    diff.Differences.Add(new SchemaDiff.DiffEntry(
                        tableName, "INFO", "N/A", "ERROR",
                        $"Could not query table info: {string.Join("; ", infoResponse.Errors.Select(e => e.ToString()))}"));
                    continue;
                }

                // INFO FOR TABLE returns an array of field/definition objects
                var infoRows = infoResponse.GetValue<List<Dictionary<string, object>>>(0);
                if (infoRows is { Count: > 0 })
                {
                    var infoJson = System.Text.Json.JsonSerializer.Serialize(infoRows);

                    // Try to parse as list of field definitions
                    try
                    {
                        var fields = System.Text.Json.JsonSerializer.Deserialize<List<InfoForTableField>>(infoJson);
                        if (fields is { Count: > 0 })
                        {
                            // Extract field names from INFO result
                            var actualFields = fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                            // Get expected fields from configured mapping
                            var mapping = _options.Schema.Mappings.Values
                                .FirstOrDefault(m => string.Equals(
                                    MetadataDispatch.GetTableName(m.DocumentType), tableName,
                                    StringComparison.OrdinalIgnoreCase));

                            if (mapping is not null)
                            {
                                var expectedFields = GetConfiguredFieldNames(mapping);

                                // Check for missing fields (configured but not in DB)
                                foreach (var field in expectedFields)
                                {
                                    if (!actualFields.Contains(field))
                                    {
                                        diff.Differences.Add(new SchemaDiff.DiffEntry(
                                            tableName, $"Field: {field}", field, "MISSING",
                                            $"Field '{field}' is configured but not defined in database"));
                                    }
                                }

                                // Check for extra fields (in DB but not configured)
                                foreach (var field in actualFields)
                                {
                                    if (!expectedFields.Contains(field))
                                    {
                                        diff.Differences.Add(new SchemaDiff.DiffEntry(
                                            tableName, $"Field: {field}", "N/A", field,
                                            $"Field '{field}' exists in database but is not configured"));
                                    }
                                }
                            }
                            else
                            {
                                // Table exists in DB but has no mapping
                                diff.Differences.Add(new SchemaDiff.DiffEntry(
                                    tableName, "TABLE", "N/A", tableName,
                                    $"Table '{tableName}' exists in database but no mapping is configured"));
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // INFO FOR TABLE returned unexpected format — record as informational
                        diff.Differences.Add(new SchemaDiff.DiffEntry(
                            tableName, "INFO", "configured", "present",
                            $"Table '{tableName}' exists (schema info retrieved, {infoRows.Count} row(s))"));
                    }
                }
                else
                {
                    // Table is configured but doesn't exist in DB
                    diff.Differences.Add(new SchemaDiff.DiffEntry(
                        tableName, "TABLE", tableName, "MISSING",
                        $"Table '{tableName}' is configured but does not exist in the database"));
                }
            }
            catch (Exception ex)
            {
                diff.Differences.Add(new SchemaDiff.DiffEntry(
                    tableName, "QUERY", "N/A", "ERROR",
                    $"Failed to query INFO FOR TABLE `{tableName}`: {ex.Message}"));
            }
        }

        return diff;
    }

    /// <summary>
    /// Extracts the set of configured field names from a document mapping for
    /// comparison against actual database fields.
    /// </summary>
    private static HashSet<string> GetConfiguredFieldNames(DocumentMapping mapping)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add ID field (always present in any document)
        fields.Add("id");

        // Add fields from the document type's public instance properties
        var docType = mapping.DocumentType;
        foreach (var prop in docType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetGetMethod() is null || prop.GetIndexParameters().Length > 0)
                continue;
            // Skip inherited Record properties (Id is already tracked as "id")
            if (string.Equals(prop.Name, "Id", StringComparison.OrdinalIgnoreCase))
                continue;
            fields.Add(prop.Name);
        }

        return fields;
    }
}

/// <summary>
/// POCO for deserializing individual field entries from INFO FOR TABLE results.
/// SurrealDB returns an array of objects with at minimum a "name" property.
/// </summary>
internal sealed class InfoForTableField
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
