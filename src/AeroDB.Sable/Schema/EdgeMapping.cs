using System.Linq.Expressions;
using System.Reflection;

namespace AeroDB.Sable;

/// <summary>Fluent configuration for a graph edge table.</summary>
public class EdgeMapping<TEdge> where TEdge : EdgeRecord
{
    /// <summary>The edge table name (e.g., "works_in").</summary>
    public string TableName { get; set; } = null!;

    /// <summary>The source/from table name (IN).</summary>
    public string FromTable { get; set; } = null!;

    /// <summary>The target/to table name (OUT).</summary>
    public string ToTable { get; set; } = null!;

    /// <summary>Schema mode — Strict or Flexible.</summary>
    public SchemaMode SchemaMode { get; set; } = SchemaMode.Flexible;

    private readonly List<string> _indexes = new();
    public IReadOnlyList<string> Indexes => _indexes;

    /// <summary>Set the schema mode for this edge table.</summary>
    public EdgeMapping<TEdge> SetSchemaMode(SchemaMode mode)
    {
        SchemaMode = mode;
        return this;
    }

    /// <summary>Add an index on an edge field.</summary>
    public EdgeMapping<TEdge> Index<TProp>(Expression<Func<TEdge, TProp>> property)
    {
        var member = ExtractMember(property);
        _indexes.Add(member.Name);
        return this;
    }

    private static MemberInfo ExtractMember<TProp>(Expression<Func<TEdge, TProp>> expression)
    {
        return expression.Body switch
        {
            MemberExpression me => me.Member,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: MemberExpression me } => me.Member,
            _ => throw new ArgumentException("Expression must refer to a property or field.")
        };
    }
}
