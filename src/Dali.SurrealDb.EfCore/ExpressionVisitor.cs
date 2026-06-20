// ============================================================
// SurrealDB EF Core LINQ Provider
// Expression Visitor & SurrealQL Translator
// ============================================================

using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SurrealEFCore.Core;

namespace SurrealEFCore.Query;

// ── Query Mode ────────────────────────────────────────────────

public enum QueryMode
{
    Document,   // SELECT * FROM table WHERE ...
    Graph,      // SELECT ->edge->target FROM source WHERE ...
    SQL         // Raw SurrealQL / analytic expressions
}

// ── Translated Query ──────────────────────────────────────────

public record TranslatedQuery(
    string         SurrealQL,
    QueryMode      Mode,
    object?        Parameters   = null,
    Type?          ElementType  = null
);

// ── Expression Visitor ────────────────────────────────────────

/// <summary>
/// Walks a LINQ expression tree and emits SurrealQL.
/// Handles WHERE, SELECT (projections), ORDER BY, SKIP/TAKE,
/// graph traversals (.Traverse()), and raw SQL escapes (.Raw()).
/// </summary>
public class SurrealExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _sb     = new();
    private readonly List<string>  _where  = new();
    private readonly List<string>  _order  = new();
    private int?   _limit;
    private int?   _start;
    private string _projection = "*";
    private string _table      = "";
    private QueryMode _mode    = QueryMode.Document;

    // ── Graph state ───────────────────────────────────────────
    private readonly List<GraphTraversalStep> _graphSteps = new();

    // ── Entry point ───────────────────────────────────────────

    public TranslatedQuery Translate(Expression expression)
    {
        Visit(expression);
        return Build();
    }

    // ── MethodCall dispatcher ─────────────────────────────────

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Where":
                Visit(node.Arguments[0]);
                var lambda = StripQuote(node.Arguments[1]) as LambdaExpression;
                _where.Add(TranslateCondition(lambda!.Body));
                break;

            case "Select":
                Visit(node.Arguments[0]);
                var proj = StripQuote(node.Arguments[1]) as LambdaExpression;
                _projection = TranslateProjection(proj!.Body);
                break;

            case "OrderBy":
            case "ThenBy":
                Visit(node.Arguments[0]);
                var asc = StripQuote(node.Arguments[1]) as LambdaExpression;
                _order.Add($"{TranslateMember(asc!.Body)} ASC");
                break;

            case "OrderByDescending":
            case "ThenByDescending":
                Visit(node.Arguments[0]);
                var desc = StripQuote(node.Arguments[1]) as LambdaExpression;
                _order.Add($"{TranslateMember(desc!.Body)} DESC");
                break;

            case "Take":
                Visit(node.Arguments[0]);
                _limit = (int)((ConstantExpression)node.Arguments[1]).Value!;
                break;

            case "Skip":
                Visit(node.Arguments[0]);
                _start = (int)((ConstantExpression)node.Arguments[1]).Value!;
                break;

            // ── Graph traversal extension method ──────────────
            case "Traverse":
                Visit(node.Arguments[0]);
                _mode = QueryMode.Graph;
                var edgeType   = node.Method.GetGenericArguments()[0];
                var targetType = node.Method.GetGenericArguments()[1];
                var direction  = (string)((ConstantExpression)node.Arguments[1]).Value!;
                _graphSteps.Add(new(edgeType, targetType, direction));
                break;

            // ── Raw SurrealQL escape hatch ─────────────────────
            case "Raw":
                Visit(node.Arguments[0]);
                _mode = QueryMode.SQL;
                var rawSql = (string)((ConstantExpression)node.Arguments[1]).Value!;
                _where.Add($"/* raw */ {rawSql}");
                break;

            default:
                Visit(node.Arguments[0]);
                break;
        }
        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value is IQueryable q)
            _table = DeriveTableName(q.ElementType);
        return node;
    }

    // ── Condition translation ─────────────────────────────────

    private string TranslateCondition(Expression expr) => expr switch
    {
        BinaryExpression b  => TranslateBinary(b),
        UnaryExpression  u  => TranslateUnary(u),
        MethodCallExpression m => TranslateMethod(m),
        _                   => throw new NotSupportedException($"Unsupported: {expr}")
    };

    private string TranslateBinary(BinaryExpression b)
    {
        var left  = TranslateOperand(b.Left);
        var right = TranslateOperand(b.Right);
        var op    = b.NodeType switch
        {
            ExpressionType.Equal              => "=",
            ExpressionType.NotEqual           => "!=",
            ExpressionType.GreaterThan        => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan           => "<",
            ExpressionType.LessThanOrEqual    => "<=",
            ExpressionType.AndAlso            => "AND",
            ExpressionType.OrElse             => "OR",
            ExpressionType.Add                => "+",
            ExpressionType.Subtract           => "-",
            _ => throw new NotSupportedException($"Operator {b.NodeType}")
        };

        // Logical: wrap sub-expressions in parens
        if (b.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return $"({left}) {op} ({right})";

        return $"{left} {op} {right}";
    }

    private string TranslateUnary(UnaryExpression u) => u.NodeType switch
    {
        ExpressionType.Not    => $"NOT ({TranslateCondition(u.Operand)})",
        ExpressionType.Convert => TranslateOperand(u.Operand),
        _ => throw new NotSupportedException($"Unary {u.NodeType}")
    };

    private string TranslateMethod(MethodCallExpression m)
    {
        // string.Contains → string::contains()
        if (m.Method.DeclaringType == typeof(string))
        {
            var obj = TranslateOperand(m.Object!);
            var arg = TranslateOperand(m.Arguments[0]);
            return m.Method.Name switch
            {
                "Contains"   => $"string::contains({obj}, {arg})",
                "StartsWith" => $"string::startsWith({obj}, {arg})",
                "EndsWith"   => $"string::endsWith({obj}, {arg})",
                _ => throw new NotSupportedException(m.Method.Name)
            };
        }

        // Enumerable.Contains → IN [...]
        if (m.Method.Name == "Contains" && m.Arguments.Count == 2)
        {
            var collection = TranslateOperand(m.Arguments[0]);
            var item       = TranslateOperand(m.Arguments[1]);
            return $"{item} IN {collection}";
        }

        // List<T>.Contains  → CONTAINS
        if (m.Method.Name == "Contains" && m.Arguments.Count == 1)
        {
            var col  = TranslateMember(m.Object!);
            var item = TranslateOperand(m.Arguments[0]);
            return $"{col} CONTAINS {item}";
        }

        throw new NotSupportedException($"Method: {m.Method.Name}");
    }

    private string TranslateOperand(Expression expr) => expr switch
    {
        ConstantExpression c   => FormatValue(c.Value),
        MemberExpression   m   => TranslateMember(m),
        UnaryExpression    u when u.NodeType == ExpressionType.Convert
                               => TranslateOperand(u.Operand),
        NewArrayExpression a   => $"[{string.Join(", ", a.Expressions.Select(e => TranslateOperand(e)))}]",
        _                      => TranslateCondition(expr)
    };

    private static string TranslateMember(Expression expr)
    {
        if (expr is MemberExpression m)
        {
            // Skip the parameter root (entity alias)
            if (m.Expression is ParameterExpression)
                return CamelCaseToSnake(m.Member.Name);

            // Nested: e.Address.City -> address.city
            return $"{TranslateMember(m.Expression!)}.{CamelCaseToSnake(m.Member.Name)}";
        }
        if (expr is ParameterExpression)
            return "*";

        throw new NotSupportedException($"Member expression: {expr}");
    }

    // ── Projection translation ────────────────────────────────

    private string TranslateProjection(Expression body)
    {
        // new { p.Name, p.Age }  → name, age
        if (body is NewExpression n)
            return string.Join(", ", n.Arguments.Select(a => TranslateMember(a)));

        // p.Name  → name
        if (body is MemberExpression)
            return TranslateMember(body);

        return "*";
    }

    // ── Graph traversal emission ──────────────────────────────

    private string BuildGraphTraversal()
    {
        // e.g. ->knows->person
        var path = _graphSteps.Select(step =>
        {
            var edgeName  = DeriveTableName(step.EdgeType);
            var targetName = DeriveTableName(step.TargetType);
            return step.Direction == "in"
                ? $"<-{edgeName}<-{targetName}"
                : $"->{edgeName}->{targetName}";
        });
        return string.Join("", path);
    }

    // ── Final query assembly ──────────────────────────────────

    private TranslatedQuery Build()
    {
        _sb.Clear();
        _sb.Append("SELECT ");

        if (_mode == QueryMode.Graph)
        {
            // SELECT ->knows->person.* FROM person:alice
            var traversal = BuildGraphTraversal();
            _sb.Append($"{traversal}.{_projection}");
        }
        else
        {
            _sb.Append(_projection);
        }

        _sb.Append($" FROM {_table}");

        if (_where.Count > 0)
            _sb.Append($" WHERE {string.Join(" AND ", _where)}");

        if (_order.Count > 0)
            _sb.Append($" ORDER BY {string.Join(", ", _order)}");

        if (_limit.HasValue) _sb.Append($" LIMIT {_limit}");
        if (_start.HasValue) _sb.Append($" START {_start}");

        return new(_sb.ToString(), _mode);
    }

    // ── Helpers ───────────────────────────────────────────────

    private static string DeriveTableName(Type t) =>
        // Person → person, ProductOrder → product_order
        CamelCaseToSnake(t.Name);

    private static string CamelCaseToSnake(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                sb.Append('_');
            sb.Append(char.ToLower(name[i]));
        }
        return sb.ToString();
    }

    private static string FormatValue(object? val) => val switch
    {
        null                  => "NONE",
        string s              => $"'{s.Replace("'", "\\'")}'",
        bool b                => b ? "true" : "false",
        DateTime dt           => $"d'{dt:yyyy-MM-ddTHH:mm:ssZ}'",
        DateTimeOffset dto    => $"d'{dto:yyyy-MM-ddTHH:mm:ssZ}'",
        IEnumerable<object> e => $"[{string.Join(", ", e.Select(FormatValue))}]",
        _                     => val.ToString()!
    };

    private static Expression StripQuote(Expression e) =>
        e.NodeType == ExpressionType.Quote
            ? ((UnaryExpression)e).Operand
            : e;

    private record GraphTraversalStep(Type EdgeType, Type TargetType, string Direction);
}
