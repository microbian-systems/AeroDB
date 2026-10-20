using AeroDB.Sable;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Response;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Regression coverage for value-free query diagnostics at the Aero.Vault read boundary.
/// The canaries are synthetic and intentionally exercise rendered, structured, and exception log capture.
/// </summary>
public sealed class VaultReadLoggingRegressionTests
{
    [Test]
    public async Task RawQueryAsync_debug_logging_excludes_sensitive_sql_identity_tenant_and_parameter_values()
    {
        const string identityCanary = "synthetic-vault-read-identity";
        const string tenantCanary = "synthetic-vault-read-tenant";
        const string valueCanary = "synthetic-vault-read-value";
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        await using var session = CreateSession(
            loggerFactory,
            Task.FromResult(new SurrealDbResponse([])));

        await session.RawQueryAsync<Person>(
            $"SELECT * FROM vault:`{identityCanary}` WHERE tenant = '{tenantCanary}' AND secret = $secret",
            new Dictionary<string, object?> { ["secret"] = valueCanary });

        capture.Rendered.ShouldNotBeEmpty();
        capture.AllCapturedText.ShouldNotContain(identityCanary);
        capture.AllCapturedText.ShouldNotContain(tenantCanary);
        capture.AllCapturedText.ShouldNotContain(valueCanary);
        capture.Rendered.ShouldContain(message =>
            message.Contains("Executing SurrealQL", StringComparison.Ordinal));
    }

    [Test]
    public async Task RawQueryAsync_database_error_details_are_not_written_to_exception_or_structured_logs()
    {
        const string errorCanary = "synthetic-vault-read-provider-error";
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        await using var session = CreateSession(
            loggerFactory,
            Task.FromResult(new SurrealDbResponse([new SensitiveErrorResult(errorCanary)])));

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => session.RawQueryAsync<Person>("SELECT * FROM vault"));

        capture.AllCapturedText.ShouldNotContain(errorCanary);
    }

    private static QuerySession CreateSession(
        ILoggerFactory loggerFactory,
        Task<SurrealDbResponse> rawQueryResult)
    {
        var provider = Substitute.For<ISurrealDbSession>();
        provider.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(rawQueryResult);

        return new QuerySession(
            Substitute.For<ISurrealDbClient>(),
            provider,
            new StoreOptions { LoggerFactory = loggerFactory },
            DocumentTracking.None);
    }

    private sealed class SensitiveErrorResult(string details) : ISurrealDbErrorResult
    {
        public bool IsOk => false;
        public override string ToString() => details;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _rendered = [];
        private readonly List<string> _structured = [];
        private readonly List<string> _exceptions = [];

        public IReadOnlyList<string> Rendered => _rendered;
        public string AllCapturedText => string.Join("\n", _rendered.Concat(_structured).Concat(_exceptions));

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_rendered, _structured, _exceptions);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(
        List<string> rendered,
        List<string> structured,
        List<string> exceptions) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            rendered.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                structured.Add(string.Join(";", fields.Select(field => $"{field.Key}={field.Value}")));
            if (exception is not null)
                exceptions.Add(exception.ToString());
        }
    }
}
