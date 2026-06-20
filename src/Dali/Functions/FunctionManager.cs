using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace Dali;

public class FunctionManager
{
    private readonly ILogger<FunctionManager> _logger;

    public FunctionManager(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<FunctionManager>()
            ?? NullLogger<FunctionManager>.Instance;
    }

    public async Task EnsureFunctionAsync(ISurrealDbSession session, SurrealFunction function, CancellationToken ct = default)
    {
        var surql = BuildDefineFunctionSurql(function);
        _logger.LogDebug("Ensuring function {Name}", function.Name);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    public async Task RemoveFunctionAsync(ISurrealDbSession session, string name, CancellationToken ct = default)
    {
        _logger.LogDebug("Removing function {Name}", name);
        await session.RawQuery($"REMOVE FUNCTION {name};", null, ct).ConfigureAwait(false);
    }

    private static string BuildDefineFunctionSurql(SurrealFunction function)
    {
        var paramPart = function.Parameters is not null ? $"({function.Parameters})" : "()";
        return $"DEFINE FUNCTION {function.Name}{paramPart} {{ {function.Body} }};";
    }
}
