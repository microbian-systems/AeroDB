namespace AeroDB.Sable;

/// <summary>
/// Defines a SurrealDB full-text analyzer (DEFINE ANALYZER).
/// </summary>
public sealed class AnalyzerDefinition
{
    /// <summary>
    /// The analyzer name, referenced by full-text indexes.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Tokenizers to apply (e.g. "blank", "class", "punct", "camel").
    /// Default when created via <see cref="AnalyzerOptions.DefineAnalyzer"/> is ["blank", "class", "punct"].
    /// </summary>
    public string[] Tokenizers { get; set; } = [];

    /// <summary>
    /// Optional filters (e.g. "SNOWBALL(en)", "LOWERCASE", "ASCII").
    /// </summary>
    public string[] Filters { get; set; } = [];
}

/// <summary>
/// Configuration for SurrealDB analyzers (DEFINE ANALYZER).
/// Accessed via <c>StoreOptions.Schema.Analyzers</c>.
/// </summary>
public class AnalyzerOptions
{
    internal List<AnalyzerDefinition> Analyzers { get; } = new();

    /// <summary>
    /// Defines a full-text analyzer for use with <see cref="IndexType.FullText"/> indexes.
    /// </summary>
    /// <param name="name">Unique analyzer name (e.g. "my_analyzer").</param>
    /// <param name="tokenizers">Tokenizers to apply (default: blank, class, punct).</param>
    /// <param name="filters">Optional filters (e.g. SNOWBALL(en)).</param>
    public AnalyzerOptions DefineAnalyzer(
        string name,
        string[]? tokenizers = null,
        string[]? filters = null)
    {
        Analyzers.Add(new AnalyzerDefinition
        {
            Name = name,
            Tokenizers = tokenizers ?? [Search.Tokenizer.Blank, Search.Tokenizer.Class, Search.Tokenizer.Punct],
            Filters = filters ?? []
        });
        return this;
    }
}
