using System.Linq.Expressions;
using System.Text;
using Dali.Metadata;

namespace Dali;

public class SurrealExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _sb = new();
    private readonly List<string> _where = new();
    private readonly List<string> _orderBy = new();
    private int? _limit;
    private int? _skip;
    private string _projection = "*";

    public SurrealQueryResult Translate(Expression expression)
    {
        _sb.Clear();
        _where.Clear();
        _orderBy.Clear();
        _limit = null;
        _skip = null;
        _projection = "*";
        TableName = null;

        Visit(expression);
        return new SurrealQueryResult
        {
            TableName = TableName,
            Where = _where,
            OrderBy = _orderBy,
            Limit = _limit,
            Skip = _skip,
            Projection = _projection
        };
    }

    public string? TableName { get; private set; }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Where":
                Visit(node.Arguments[0]);
                var lambda = StripQuote(node.Arguments[1]) as LambdaExpression;
                if (lambda?.Body is not null)
                    _where.Add(TranslateCondition(lambda.Body));
                break;

            case "OrderBy":
            case "ThenBy":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression ascLambda
                    && ascLambda.Body is MemberExpression ascMember)
                    _orderBy.Add($"{ascMember.Member.Name} ASC");
                break;

            case "OrderByDescending":
            case "ThenByDescending":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression descLambda
                    && descLambda.Body is MemberExpression descMember)
                    _orderBy.Add($"{descMember.Member.Name} DESC");
                break;

            case "Take":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is ConstantExpression takeVal)
                    _limit = (int)takeVal.Value!;
                break;

            case "Skip":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is ConstantExpression skipVal)
                    _skip = (int)skipVal.Value!;
                break;

            case "Select":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression selLambda)
                {
                    if (selLambda.Body is NewExpression newExpr)
                        _projection = string.Join(", ", newExpr.Arguments.Select(ProjMember));
                    else if (selLambda.Body is MemberInitExpression init)
                        _projection = string.Join(", ", init.Bindings
                            .OfType<MemberAssignment>()
                            .Select(b => $"{ProjMember(b.Expression)} AS {b.Member.Name}"));
                }
                break;

            case "Sum":
            case "Min":
            case "Max":
            case "Average":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression aggLambda
                    && aggLambda.Body is MemberExpression aggMember)
                {
                    var fn = node.Method.Name switch
                    {
                        "Sum" => "math::sum",
                        "Min" => "math::min",
                        "Max" => "math::max",
                        "Average" => "math::mean",
                        _ => node.Method.Name
                    };
                    _projection = $"{fn}({aggMember.Member.Name})";
                }
                break;

            default:
                if (node.Arguments.Count > 0)
                    Visit(node.Arguments[0]);
                break;
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value is IQueryable q)
            TableName = MetadataDispatch.GetTableName(q.ElementType);
        return node;
    }

    private static string TranslateCondition(Expression expr) => expr switch
    {
        BinaryExpression b => TranslateBinary(b),
        MethodCallExpression m => TranslateMethod(m),
        UnaryExpression u when u.NodeType == ExpressionType.Not
            => $"NOT ({TranslateCondition(u.Operand)})",
        MemberExpression m => m.Member.Name,
        _ => ""
    };

    private static string TranslateBinary(BinaryExpression b)
    {
        var left = Operand(b.Left);
        var right = Operand(b.Right);
        var op = b.NodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso => "AND",
            ExpressionType.OrElse => "OR",
            ExpressionType.Add => "+",
            ExpressionType.Subtract => "-",
            ExpressionType.Multiply => "*",
            ExpressionType.Divide => "/",
            ExpressionType.Modulo => "%",
            _ => throw new NotSupportedException($"Operator {b.NodeType}")
        };

        if (b.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return $"({left}) {op} ({right})";

        return $"{left} {op} {right}";
    }

    private static string TranslateMethod(MethodCallExpression m)
    {
        if (m.Method.DeclaringType == typeof(string))
        {
            var obj = Operand(m.Object!);
            var arg = Operand(m.Arguments[0]);
            return m.Method.Name switch
            {
                "Contains" => $"string::contains({obj}, {arg})",
                "StartsWith" => $"string::startsWith({obj}, {arg})",
                "EndsWith" => $"string::endsWith({obj}, {arg})",
                _ => throw new NotSupportedException($"String.{m.Method.Name}")
            };
        }

        if (m.Method.Name == "Contains" && m.Arguments.Count == 1)
        {
            var col = Operand(m.Object!);
            var item = Operand(m.Arguments[0]);
            return $"{col} CONTAINS {item}";
        }

        throw new NotSupportedException($"Method {m.Method.Name}");
    }

    private static string Operand(Expression expr) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value),
        MemberExpression m => MemberPath(m),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand),
        _ => TranslateCondition(expr)
    };

    private static string MemberPath(MemberExpression m)
    {
        if (m.Expression is ParameterExpression)
            return m.Member.Name;
        if (m.Expression is MemberExpression inner)
        {
            var innerPath = MemberPath(inner);
            if (IsDateTimeMember(m))
                return DateTimeFunc(m.Member.Name, innerPath);
            return $"{innerPath}.{m.Member.Name}";
        }
        return m.Member.Name;
    }

    private static bool IsDateTimeMember(MemberExpression m)
        => m.Member.DeclaringType == typeof(DateTime) || m.Member.DeclaringType == typeof(DateTimeOffset);

    private static string DateTimeFunc(string member, string operand) => member switch
    {
        "Year" => $"time::year({operand})",
        "Month" => $"time::month({operand})",
        "Day" => $"time::day({operand})",
        "DayOfWeek" => $"time::wday({operand})",
        "Hour" => $"time::hour({operand})",
        "Minute" => $"time::minute({operand})",
        "Second" => $"time::second({operand})",
        _ => $"{operand}.{member}"
    };

    private static string ProjMember(Expression expr)
    {
        if (expr is MemberExpression m)
            return m.Member.Name;
        return "*";
    }

    private static string FormatValue(object? val) => val switch
    {
        null => "NONE",
        string s => $"'{s.Replace("'", "\\'")}'",
        bool b => b ? "true" : "false",
        int or long or short or byte or float or double or decimal => val.ToString()!,
        DateTime dt => $"d'{dt:yyyy-MM-ddTHH:mm:ssZ}'",
        DateTimeOffset dto => $"d'{dto:yyyy-MM-ddTHH:mm:ssZ}'",
        _ => val.ToString()!
    };

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }

    private static Expression StripQuote(Expression e)
        => e.NodeType == ExpressionType.Quote ? ((UnaryExpression)e).Operand : e;
}

public class SurrealQueryResult
{
    public string? TableName { get; set; }
    public List<string> Where { get; set; } = [];
    public List<string> OrderBy { get; set; } = [];
    public int? Limit { get; set; }
    public int? Skip { get; set; }
    public string Projection { get; set; } = "*";
    public bool GroupAll { get; set; }

    public string ToSurrealQL()
    {
        var sb = new StringBuilder();
        sb.Append("SELECT ");
        sb.Append(Projection);
        sb.Append(" FROM `");
        sb.Append(TableName ?? "unknown");
        sb.Append('`');

        if (Where.Count > 0)
        {
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", Where));
        }

        if (GroupAll)
        {
            sb.Append(" GROUP ALL");
        }

        if (OrderBy.Count > 0)
        {
            sb.Append(" ORDER BY ");
            sb.Append(string.Join(", ", OrderBy));
        }

        if (Limit.HasValue)
        {
            sb.Append(" LIMIT ");
            sb.Append(Limit.Value);
        }

        if (Skip.HasValue)
        {
            sb.Append(" START ");
            sb.Append(Skip.Value);
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Creates a deep clone of this query result, including copies of all mutable collections.
    /// Thread-safe for concurrent execution of compiled queries.
    /// </summary>
    public SurrealQueryResult Clone()
    {
        return new SurrealQueryResult
        {
            TableName = TableName,
            Where = [..Where],
            OrderBy = [..OrderBy],
            Limit = Limit,
            Skip = Skip,
            Projection = Projection,
            GroupAll = GroupAll
        };
    }
}
