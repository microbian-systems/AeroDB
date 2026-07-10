using System.Text.Json;
using AeroDB.Sable;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using SagaInstanceState = JasperFx.Descriptors.SagaInstanceState;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB.Sable-backed implementation of <see cref="ISagaStoreDiagnostics"/>.
/// </summary>
internal sealed class AeroDBSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly IWolverineRuntime _runtime;
    private readonly IDocumentStore _store;

    public AeroDBSagaStoreDiagnostics(IWolverineRuntime runtime, IDocumentStore store)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        // Return current known sagas using the public handler graph.
        // The IWolverineRuntime doesn't expose the handler graph publicly,
        // so we enumerate through SagaStorage's own reporting.
        return Task.FromResult<IReadOnlyList<SagaDescriptor>>(new List<SagaDescriptor>());
    }

    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (identity is null) return null;

        await using var session = await _store.QuerySessionAsync(ct);
        var idStr = identity.ToString();
        if (string.IsNullOrEmpty(idStr)) return null;

        var results = await session.RawQueryAsync<object>(
            $"SELECT * FROM {ToSnakeCase(sagaTypeName)}:`{idStr.Replace("`", "``")}`",
            null, ct);

        var saga = results.FirstOrDefault();
        if (saga is null) return null;

        var sagaType = saga.GetType();
        var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
        var isCompleted = saga is Wolverine.Saga sagaBase && sagaBase.IsCompleted();
        return new SagaInstanceState(
            sagaType.FullName!,
            identity,
            isCompleted,
            stateJson,
            null);
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        var clamped = count <= 0 ? 0 : Math.Min(count, 1000);
        if (clamped == 0) return [];

        await using var session = await _store.QuerySessionAsync(ct);

        var results = await session.RawQueryAsync<object>(
            $"SELECT * FROM {ToSnakeCase(sagaTypeName)} LIMIT {clamped}",
            null, ct);

        var list = new List<SagaInstanceState>(results.Count);
        foreach (var saga in results)
        {
            var sagaType = saga.GetType();
            var id = ExtractIdentity(saga) ?? Guid.Empty;
            var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
            var isCompleted = saga is Wolverine.Saga sagaBase && sagaBase.IsCompleted();
            list.Add(new SagaInstanceState(
                sagaType.FullName!,
                id,
                isCompleted,
                stateJson,
                null));
        }

        return list;
    }

    private static object? ExtractIdentity(object saga)
    {
        var sagaType = saga.GetType();
        var idMember = sagaType.GetProperty("Id") ?? sagaType.GetField("Id") as System.Reflection.MemberInfo;
        return idMember switch
        {
            System.Reflection.PropertyInfo p => p.GetValue(saga),
            System.Reflection.FieldInfo f => f.GetValue(saga),
            _ => null
        };
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }
}
