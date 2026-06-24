using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Dali.Metadata;

namespace Dali;

public sealed class DaliMlQuery<TInput, TOutput> : IMlQuery<TInput, TOutput>
    where TInput : class
    where TOutput : class
{
    private readonly SurrealQueryProvider _provider;
    private readonly string _table;

    private string? _modelName;
    private string? _modelVersion;
    private Expression<Func<TInput, object>>? _inputMapping;
    private string? _whereClause;
    private int _limit = 1000;
    private int _skip;

    private readonly SurrealCommandBuilder _paramBuilder = new();

    internal DaliMlQuery(SurrealQueryProvider provider)
    {
        _provider = provider;
        _table = MetadataDispatch.GetTableName(typeof(TInput));
    }

    public IMlQuery<TInput, TOutput> Model(string name, string version)
    {
        _modelName = name;
        _modelVersion = version;
        return this;
    }

    public IMlQuery<TInput, TOutput> Input(Expression<Func<TInput, object>> inputMapping)
    {
        _inputMapping = inputMapping;
        return this;
    }

    public IMlQuery<TInput, TOutput> Where(Expression<Func<TInput, bool>> predicate)
    {
        _whereClause = SurrealExpressionVisitor.TranslateCondition(predicate.Body, _paramBuilder);
        return this;
    }

    public IMlQuery<TInput, TOutput> Take(int limit)
    {
        _limit = limit;
        return this;
    }

    public IMlQuery<TInput, TOutput> Skip(int count)
    {
        _skip = count;
        return this;
    }

    public async Task<TOutput> ComputeAsync(TInput input, CancellationToken ct = default)
    {
        if (_modelName is null || _modelVersion is null)
            throw new InvalidOperationException("Model name and version must be set via .Model() before executing.");
        if (_inputMapping is null)
            throw new InvalidOperationException("Input mapping must be set via .Input() before executing.");

        var bindings = BuildInputBindingsForObject(input);
        var surql = $"RETURN ml::{_modelName}<{_modelVersion}>({bindings});";

        var response = await _provider.Session.RawQuery(surql, _paramBuilder.Parameters, ct).ConfigureAwait(false);
        if (!response.HasErrors && response.Count > 0)
        {
            var result = response.GetValue<TOutput>(0);
            if (result is not null) return result;
        }
        if (response.HasErrors)
        {
            var firstError = response.GetValue<string>(0);
            if (firstError?.Contains("Machine learning computation is not enabled") == true)
                throw new MlNotAvailableException("ML computation is not available on this SurrealDB server.");
        }
        return default!;
    }

    public async Task<List<TOutput>> ComputeAllAsync(CancellationToken ct = default)
    {
        if (_modelName is null || _modelVersion is null)
            throw new InvalidOperationException("Model name and version must be set via .Model() before executing.");
        if (_inputMapping is null)
            throw new InvalidOperationException("Input mapping must be set via .Input() before executing.");

        var columnBindings = BuildInputBindingsForColumns();
        var sb = new StringBuilder();
        sb.Append($"SELECT *, ml::{_modelName}<{_modelVersion}>({columnBindings}) AS _ml_result FROM `{_table}`");

        if (_whereClause is not null)
            sb.Append($" WHERE ({_whereClause})");

        sb.Append($" LIMIT {_limit}");
        if (_skip > 0)
            sb.Append($" START AT {_skip}");

        sb.Append(';');

        var response = await _provider.Session.RawQuery(sb.ToString(), _paramBuilder.Parameters, ct).ConfigureAwait(false);
        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<TOutput>>(0);
            if (raw is not null) return raw;
        }
        return [];
    }

    private string BuildInputBindingsForObject(TInput input)
    {
        if (_inputMapping is null) throw new InvalidOperationException("No input mapping configured.");
        var compiled = _inputMapping.Compile();
        var result = compiled(input);
        return SerializeToSurrealQLObject(result);
    }

    private string BuildInputBindingsForColumns()
    {
        if (_inputMapping is null) throw new InvalidOperationException("No input mapping configured.");

        var body = _inputMapping.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            body = unary.Operand;

        if (body is NewExpression newExpr)
        {
            var parts = new List<string>();
            var param = _inputMapping.Parameters[0];
            for (int i = 0; i < newExpr.Members!.Count; i++)
            {
                var memberName = newExpr.Members[i].Name;
                var arg = newExpr.Arguments[i];
                string columnName;
                if (arg is MemberExpression m && m.Expression == param)
                    columnName = m.Member.Name;
                else
                    columnName = memberName;
                parts.Add($"{memberName}: {columnName}");
            }
            return "{ " + string.Join(", ", parts) + " }";
        }

        throw new InvalidOperationException("Input mapping must be a 'new { ... }' expression for batch inference.");
    }

    private static string SerializeToSurrealQLObject(object obj)
    {
        if (obj is null) return "NONE";

        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return ConvertJsonToSurrealQL(json);
    }

    internal static string ConvertJsonToSurrealQL(string json)
    {
        var sb = new StringBuilder();
        var inKey = false;
        var keyStart = 0;
        for (int i = 0; i < json.Length; i++)
        {
            var ch = json[i];
            if (ch == '{') { sb.Append("{ "); continue; }
            if (ch == '}') { sb.Append(" }"); continue; }
            if (ch == '"')
            {
                if (!inKey)
                {
                    inKey = true;
                    keyStart = i + 1;
                    continue;
                }
                else
                {
                    var key = json.Substring(keyStart, i - keyStart);
                    sb.Append(key);
                    inKey = false;
                    continue;
                }
            }
            if (ch == ':' && !inKey) { sb.Append(": "); continue; }
            if (ch == ',' && !inKey) { sb.Append(", "); continue; }
            if (!inKey) sb.Append(ch);
        }
        return sb.ToString();
    }
}
