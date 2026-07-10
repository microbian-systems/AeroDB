using System.Linq.Expressions;

namespace AeroDB.Sable;

/// <summary>Fluent graph query builder for SurrealDB traversal.</summary>
public interface IGraphQuery<TNode> where TNode : class
{
    /// <summary>Forward traversal: SELECT ->edge->target FROM ...</summary>
    IGraphQuery<TTarget> Out<TTarget>(string edgeType) where TTarget : class;

    /// <summary>Forward traversal using the edge table name inferred from TEdge.</summary>
    IGraphQuery<TTarget> Out<TTarget, TEdge>() where TTarget : class where TEdge : EdgeRecord;

    /// <summary>Backward traversal: <c>SELECT &lt;-edge&lt;-target FROM ...</c></summary>
    IGraphQuery<TTarget> In<TTarget>(string edgeType) where TTarget : class;

    /// <summary>Backward traversal using the edge table name inferred from TEdge.</summary>
    IGraphQuery<TTarget> In<TTarget, TEdge>() where TTarget : class where TEdge : EdgeRecord;

    /// <summary>Bidirectional traversal: <c>SELECT &lt;-&gt;edge&lt;-&gt;target FROM ...</c></summary>
    IGraphQuery<TTarget> Both<TTarget>(string edgeType) where TTarget : class;

    /// <summary>Bidirectional traversal using the edge table name inferred from TEdge.</summary>
    IGraphQuery<TTarget> Both<TTarget, TEdge>() where TTarget : class where TEdge : EdgeRecord;

    /// <summary>Wildcard outgoing: <c>SELECT -&gt;?-&gt;... FROM ...</c></summary>
    IGraphQuery<GraphNode> OutAny();

    /// <summary>Wildcard incoming: <c>SELECT &lt;-?&lt;-... FROM ...</c></summary>
    IGraphQuery<GraphNode> InAny();

    /// <summary>Any edge type (no direction constraint).</summary>
    IGraphQuery<GraphNode> AnyEdge();

    /// <summary>Multiple edge types: SELECT ->(e1, e2)->...</summary>
    IGraphQuery<TTarget> Out<TTarget>(string[] edgeTypes) where TTarget : class;

    /// <summary>Fixed recursion depth: @.{n}</summary>
    IGraphQuery<TNode> Depth(int depth);

    /// <summary>Range recursion depth: @.{min..max}</summary>
    IGraphQuery<TNode> Depth(int min, int max);

    /// <summary>Open-ended recursion: @.{..}</summary>
    IGraphQuery<TNode> Depth();

    /// <summary>Shortest path to target: @.{..+shortest=id}</summary>
    IGraphQuery<TNode> ShortestPath(string recordId);

    /// <summary>Return full paths: +path</summary>
    IGraphQuery<TNode> ReturnPath();

    /// <summary>Collect unique nodes: +collect</summary>
    IGraphQuery<TNode> CollectAll();

    /// <summary>Include intermediate nodes: (+)</summary>
    IGraphQuery<TNode> IncludeIntermediate();

    /// <summary>Include origin node: +inclusive</summary>
    IGraphQuery<TNode> IncludeOrigin();

    /// <summary>Eager load relations: FETCH</summary>
    IGraphQuery<TNode> Fetch(params string[] relations);

    /// <summary>Add a WHERE filter expression (delegates to LINQ). Only valid on the first step.</summary>
    IGraphQuery<TNode> Where(Expression<Func<TNode, bool>> predicate);

    // Terminal operations

    /// <summary>Executes the graph traversal and returns all matching nodes.</summary>
    Task<List<TNode>> ToListAsync(CancellationToken ct = default);

    /// <summary>Executes the graph traversal and returns the first matching node, or null.</summary>
    Task<TNode?> FirstOrDefaultAsync(CancellationToken ct = default);

    /// <summary>Executes the graph traversal and returns matching paths.</summary>
    Task<List<GraphPath>> ToPathListAsync(CancellationToken ct = default);

    /// <summary>Executes the graph traversal and returns the count of matching results.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
