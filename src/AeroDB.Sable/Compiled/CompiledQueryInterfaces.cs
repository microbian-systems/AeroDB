using System.Linq.Expressions;

namespace AeroDB.Sable;

/// <summary>
/// Marker interface for all compiled query types.
/// Implemented by <see cref="ICompiledQuery{TDoc,TOut}"/> and its descendants.
/// Used for discovery and type checks.
/// </summary>
public interface ICompiledQueryMarker
{
}

/// <summary>
/// Core compiled query interface. The query type has properties that serve as
/// parameters, and <see cref="QueryIs"/> returns an expression tree that
/// translates those parameters into a LINQ query.
/// </summary>
/// <typeparam name="TDoc">The document type being queried.</typeparam>
/// <typeparam name="TOut">The result type (single element for scalar queries,
/// <c>IEnumerable&lt;TOut&gt;</c> for list queries).</typeparam>
public interface ICompiledQuery<TDoc, TOut> : ICompiledQueryMarker
    where TDoc : class
{
    /// <summary>
    /// Returns the LINQ expression that defines the query.
    /// The query object's property values are captured in the expression tree
    /// and replaced with parameterized SurrealQL at plan-build time.
    /// </summary>
    Expression<Func<ISableQueryable<TDoc>, TOut>> QueryIs();
}

/// <summary>
/// Compiled list query that returns <c>IEnumerable&lt;TOut&gt;</c> with an
/// optional <c>Select</c> transform.
/// </summary>
/// <typeparam name="TDoc">The document type being queried.</typeparam>
/// <typeparam name="TOut">The result element type (after Select, if any).</typeparam>
public interface ICompiledListQuery<TDoc, TOut> : ICompiledQuery<TDoc, IEnumerable<TOut>>
    where TDoc : class
{
}

/// <summary>
/// Compiled list query that returns whole documents (no Select).
/// Shorthand for <c>ICompiledListQuery&lt;TDoc, TDoc&gt;</c>.
/// </summary>
/// <typeparam name="TDoc">The document type being queried and returned.</typeparam>
public interface ICompiledListQuery<TDoc> : ICompiledListQuery<TDoc, TDoc>
    where TDoc : class
{
}

/// <summary>
/// Compiled query that returns a single document.
/// Shorthand for <c>ICompiledQuery&lt;TDoc, TDoc&gt;</c>.
/// </summary>
/// <typeparam name="TDoc">The document type being queried and returned.</typeparam>
public interface ICompiledQuery<TDoc> : ICompiledQuery<TDoc, TDoc>
    where TDoc : class
{
}

/// <summary>
/// Optional interface that compiled queries can implement to provide custom
/// sentinel values for each property during plan building. When implemented,
/// the planner calls <see cref="SetUniqueValuesForQueryPlanning"/> after
/// setting sentinel values on all public properties, allowing the query type
/// to override or customize the sentinel values used for parameter mapping.
/// </summary>
public interface IQueryPlanning
{
    /// <summary>
    /// Called once during plan building, after the planner has set sentinel
    /// values on all public readable properties. Implementations can override
    /// specific property values to ensure distinguishable sentinels (e.g.,
    /// for properties whose types don't have default sentinels).
    /// </summary>
    void SetUniqueValuesForQueryPlanning();
}
