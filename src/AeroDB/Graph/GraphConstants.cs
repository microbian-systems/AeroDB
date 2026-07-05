namespace AeroDB;

/// <summary>
/// Named constants for graph operations to avoid magic strings.
/// </summary>
public static class Graph
{
    /// <summary>Edge relation table names.</summary>
    public static class Edge
    {
        /// <summary>"knows" — social acquaintance edge.</summary>
        public const string Knows = "knows";

        /// <summary>"works_in" — employment/organization edge.</summary>
        public const string WorksIn = "works_in";

        /// <summary>"child_of" — parent-child hierarchy edge.</summary>
        public const string ChildOf = "child_of";

        /// <summary>"created" — content authorship edge.</summary>
        public const string Created = "created";
    }
}
