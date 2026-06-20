namespace Dali;

internal enum GraphDirection
{
    Out,
    In,
    Both
}

internal enum GraphTargetKind
{
    Typed,
    Any,
    MultiEdge
}

internal sealed class GraphStep
{
    public GraphDirection Direction { get; set; }
    public string? EdgeType { get; set; } // null for wildcard
    public string[]? EdgeTypes { get; set; } // for multi-edge
    public Type? TargetType { get; set; } // null for wildcard
    public GraphTargetKind TargetKind { get; set; }
    public bool IncludeIntermediate { get; set; }
}

internal sealed class GraphQueryPlan
{
    public List<GraphStep> Steps { get; set; } = new();
    public Type NodeType { get; set; } = null!;
    public int? Depth { get; set; } // Fixed depth
    public int? DepthMin { get; set; } // Range min
    public int? DepthMax { get; set; } // Range max
    public bool IsUnboundedDepth { get; set; } // {..} open-ended
    public string? ShortestPathTarget { get; set; }
    public bool ReturnPath { get; set; }
    public bool CollectAll { get; set; }
    public bool IncludeOrigin { get; set; }
    public string[]? FetchRelations { get; set; }
    public string? WhereClause { get; set; } // Pre-translated WHERE clause
    public string? FilterSurql { get; set; } // SurrealQL WHERE as string
}
