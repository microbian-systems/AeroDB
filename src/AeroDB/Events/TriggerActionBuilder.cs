using System.Linq.Expressions;
using System.Text;

namespace AeroDB;

/// <summary>
/// Fluent builder for SurrealDB <c>DEFINE EVENT</c> trigger action bodies.
/// Supports typed CREATE, UPDATE, DELETE, INSERT, and RELATE statements
/// with compile-time-safe field references via expression trees.
/// </summary>
public class TriggerActionBuilder
{
    private readonly StringBuilder _sb = new();

    /// <summary>Starts a <c>CREATE table SET field = value, ...</c> statement.</summary>
    public TriggerCreateStatement<T> Create<T>() where T : class
    {
        var table = Metadata.MetadataDispatch.GetTableName(typeof(T));
        _sb.Append($"CREATE {table}");
        return new TriggerCreateStatement<T>(this, _sb);
    }

    /// <summary>Starts an <c>UPDATE table SET field = value, ...</c> statement.</summary>
    public TriggerUpdateStatement<T> Update<T>() where T : class
    {
        var table = Metadata.MetadataDispatch.GetTableName(typeof(T));
        _sb.Append($"UPDATE {table}");
        return new TriggerUpdateStatement<T>(this, _sb);
    }

    /// <summary>Starts a <c>DELETE table WHERE ...</c> statement.</summary>
    public TriggerDeleteStatement<T> Delete<T>() where T : class
    {
        var table = Metadata.MetadataDispatch.GetTableName(typeof(T));
        _sb.Append($"DELETE {table}");
        return new TriggerDeleteStatement<T>(this, _sb);
    }

    /// <summary>Starts an <c>INSERT INTO table CONTENT ...</c> statement.</summary>
    public TriggerActionBuilder Insert<T>(object content) where T : class
    {
        var table = Metadata.MetadataDispatch.GetTableName(typeof(T));
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
        var json = System.Text.Json.JsonSerializer.Serialize(content, jsonOpts);
        _sb.Append($"INSERT INTO {table} CONTENT {json}");
        return this;
    }

    /// <summary>Starts a <c>RELATE from-&gt;edge-&gt;to CONTENT ...</c> statement.</summary>
    public TriggerRelateStatement Relate<TFrom, TEdge, TTo>(string from, string to)
        where TFrom : class
        where TEdge : class
        where TTo : class
    {
        var edgeTable = Metadata.MetadataDispatch.GetTableName(typeof(TEdge));
        var fromTable = Metadata.MetadataDispatch.GetTableName(typeof(TFrom));
        var toTable = Metadata.MetadataDispatch.GetTableName(typeof(TTo));
        _sb.Append($"RELATE {fromTable}:{from}->{edgeTable}->{toTable}:{to}");
        return new TriggerRelateStatement(this, _sb);
    }

    /// <summary>Appends a raw SurrealQL fragment (escape hatch).</summary>
    public TriggerActionBuilder Raw(string surql)
    {
        _sb.Append(surql);
        return this;
    }

    /// <summary>Returns the built SurrealQL action body.</summary>
    internal string Build() => _sb.ToString();
}

/// <summary>Builder for CREATE statements within a trigger action.</summary>
public class TriggerCreateStatement<T> where T : class
{
    private readonly TriggerActionBuilder _parent;
    private readonly StringBuilder _sb;
    private bool _first = true;

    internal TriggerCreateStatement(TriggerActionBuilder parent, StringBuilder sb)
    {
        _parent = parent;
        _sb = sb;
    }

    public TriggerCreateStatement<T> Set(Expression<Func<T, object?>> field, string value)
    {
        var name = GetFieldName(field.Body);
        _sb.Append(_first ? " SET " : ", ");
        _sb.Append($"{name} = {value}");
        _first = false;
        return this;
    }

    public TriggerCreateStatement<T> Content(object data)
    {
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
        var json = System.Text.Json.JsonSerializer.Serialize(data, jsonOpts);
        _sb.Append($" CONTENT {json}");
        return this;
    }

    public TriggerActionBuilder End() => _parent;

    private static string GetFieldName(Expression body)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            body = unary.Operand;
        if (body is MemberExpression m) return m.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression");
    }
}

/// <summary>Builder for UPDATE statements within a trigger action.</summary>
public class TriggerUpdateStatement<T> where T : class
{
    private readonly TriggerActionBuilder _parent;
    private readonly StringBuilder _sb;
    private bool _first = true;

    internal TriggerUpdateStatement(TriggerActionBuilder parent, StringBuilder sb)
    {
        _parent = parent;
        _sb = sb;
    }

    public TriggerUpdateStatement<T> Set(Expression<Func<T, object?>> field, string value)
    {
        var name = GetFieldName(field.Body);
        _sb.Append(_first ? " SET " : ", ");
        _sb.Append($"{name} = {value}");
        _first = false;
        return this;
    }

    public TriggerUpdateStatement<T> Where(Expression<Func<T, bool>> predicate)
    {
        _sb.Append(" WHERE ");
        _sb.Append(SurrealExpressionVisitor.TranslateCondition(predicate.Body));
        return this;
    }

    public TriggerActionBuilder End() => _parent;

    private static string GetFieldName(Expression body)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            body = unary.Operand;
        if (body is MemberExpression m) return m.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression");
    }
}

/// <summary>Builder for DELETE statements within a trigger action.</summary>
public class TriggerDeleteStatement<T> where T : class
{
    private readonly TriggerActionBuilder _parent;
    private readonly StringBuilder _sb;

    internal TriggerDeleteStatement(TriggerActionBuilder parent, StringBuilder sb)
    {
        _parent = parent;
        _sb = sb;
    }

    public TriggerDeleteStatement<T> Where(Expression<Func<T, bool>> predicate)
    {
        _sb.Append(" WHERE ");
        _sb.Append(SurrealExpressionVisitor.TranslateCondition(predicate.Body));
        return this;
    }

    public TriggerActionBuilder End() => _parent;
}

/// <summary>Builder for RELATE statements within a trigger action.</summary>
public class TriggerRelateStatement
{
    private readonly TriggerActionBuilder _parent;
    private readonly StringBuilder _sb;

    internal TriggerRelateStatement(TriggerActionBuilder parent, StringBuilder sb)
    {
        _parent = parent;
        _sb = sb;
    }

    public TriggerRelateStatement Content(object data)
    {
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
        var json = System.Text.Json.JsonSerializer.Serialize(data, jsonOpts);
        _sb.Append($" CONTENT {json}");
        return this;
    }

    public TriggerActionBuilder End() => _parent;
}
