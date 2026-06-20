namespace Dali;

/// <summary>
/// Well-known constants for SurrealDB search configuration.
/// Use these instead of raw strings for type safety and IntelliSense.
/// </summary>
public static class Search
{
    /// <summary>Full-text tokenizers for DEFINE ANALYZER.</summary>
    public static class Tokenizer
    {
        public const string Blank = "blank";
        public const string Class = "class";
        public const string Camel = "camel";
        public const string Punct = "punct";
        public const string Whitespace = "whitespace";
        public const string EdgeNgram2 = "edgengram-2";
        public const string EdgeNgram3 = "edgengram-3";
    }

    /// <summary>Full-text token filters for DEFINE ANALYZER.</summary>
    public static class Filter
    {
        public const string SnowballEnglish = "SNOWBALL(en)";
        public const string SnowballFrench = "SNOWBALL(fr)";
        public const string SnowballGerman = "SNOWBALL(de)";
        public const string SnowballSpanish = "SNOWBALL(es)";
        public const string Lowercase = "LOWERCASE";
        public const string Uppercase = "UPPERCASE";
        public const string Ascii = "ASCII";
        public const string Ngram2 = "NGRAMS(2)";
        public const string Ngram3 = "NGRAMS(3)";
    }

    /// <summary>Pre-defined analyzer names.</summary>
    public static class Analyzer
    {
        public const string Simple = "simple";
        public const string English = "english";
        public const string Arabic = "arabic";
        public const string Chinese = "chinese";
    }

    /// <summary>Distance functions for HNSW vector indexes.</summary>
    public static class Distance
    {
        public const string Cosine = "COSINE";
        public const string Euclidean = "EUCLIDEAN";
        public const string Manhattan = "MANHATTAN";
        public const string Minkowski = "MINKOWSKI";
        public const string Hamming = "HAMMING";
        public const string Jaccard = "JACCARD";
    }
}
