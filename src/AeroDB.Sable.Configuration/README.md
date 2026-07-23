# AeroDB.Sable.Configuration

Encrypted .NET configuration backed by persistent embedded SurrealDB
SurrealKV.

This package is an in-process encrypted configuration store. It protects
copied database files, backups, and database-only access. It is not the future
remote [`AeroDB.Sable.Vault`](https://github.com/microbian-systems/AeroVault)
security boundary: the application process can
access the plaintext configuration and its wrapping-key provider.

## Provision values

Bootstrap paths and key IDs must come from outside the encrypted store.

```csharp
using AeroDB.Sable.Configuration;

var options = new SableConfigurationOptions
{
    DatabasePath = "/var/lib/my-app/sable-configuration"
};

options.UseMountedKeyFile(
    "/run/secrets/sable-configuration-kek",
    keyId: "config-kek-2026-07");

using var store = new SableConfigurationStore(options);

await store.SetAsync(
    "ConnectionStrings:Primary",
    "Server=database;Database=application;...");

await store.SetAsync("Payments:ApiKey", paymentApiKey);
```

The mounted key file must contain either exactly 32 raw bytes or the Base64
encoding of exactly 32 bytes. The package never auto-generates or replaces the
KEK.

## Add the provider

```csharp
using AeroDB.Sable.Configuration;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

var databasePath =
    builder.Configuration["SableConfiguration:DatabasePath"]
    ?? throw new InvalidOperationException(
        "SableConfiguration:DatabasePath is required.");

var keyFile =
    builder.Configuration["SableConfiguration:KeyFile"]
    ?? throw new InvalidOperationException(
        "SableConfiguration:KeyFile is required.");

builder.Configuration.AddSableConfiguration(options =>
{
    options.DatabasePath = databasePath;
    options.UseMountedKeyFile(keyFile, keyId: "config-kek-2026-07");
});
```

.NET configuration providers use last-added-wins precedence. In the example,
Sable values override matching `appsettings.json`, environment, User Secrets,
and command-line values already registered by `WebApplication.CreateBuilder`.
This is normally desirable for values deliberately moved into the encrypted
store.

If deployment environment variables must override Sable values, explicitly
add that provider after Sable:

```csharp
builder.Configuration
    .AddSableConfiguration(options)
    .AddEnvironmentVariables(prefix: "MYAPP_");
```

Across platforms, an environment hierarchy such as `Payments:ApiKey` is
written as `MYAPP_Payments__ApiKey`.

## Consume values

Consumers use the ordinary .NET configuration APIs; they do not depend on the
storage provider.

```csharp
string? connectionString =
    builder.Configuration.GetConnectionString("Primary");

string? paymentApiKey =
    builder.Configuration["Payments:ApiKey"];
```

For related settings, prefer the options pattern:

```csharp
public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    public required Uri Endpoint { get; set; }
    public required string ApiKey { get; set; }
}

builder.Services
    .AddOptions<PaymentsOptions>()
    .Bind(builder.Configuration.GetRequiredSection(PaymentsOptions.SectionName))
    .Validate(
        static options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "Payments:ApiKey is required.")
    .ValidateOnStart();
```

`IOptions<T>` is a read-once singleton view. Use
`IOptionsMonitor<T>` for singleton consumers that must observe explicit Sable
refreshes, or `IOptionsSnapshot<T>` for scoped consumers.

## Refresh

V1 refresh is explicit; the package does not start a polling loop.

```csharp
var root = (IConfigurationRoot)builder.Configuration;
var sableProvider = root.Providers
    .OfType<SableConfigurationProvider>()
    .Single();

bool changed = await sableProvider.RefreshAsync();
```

When values changed, `RefreshAsync` calls the standard configuration reload
token. Bound `IOptionsMonitor<T>` instances receive the change.

## Writes are asynchronous

`.NET IConfiguration` is a read-only unified view and isn't designed as a
programmatic persistence API. Assigning through its indexer is rejected by
this provider. Persist changes with `ISableConfigurationStore.SetAsync` or
`DeleteAsync`, then call `RefreshAsync`.

## Development secrets

ASP.NET Core User Secrets is still useful for development bootstrap paths and
non-production values, but Microsoft documents that User Secrets is an
unencrypted JSON file and isn't a trusted store. Do not use production values
in development or commit secrets to `appsettings.json`.

References:

- [Configuration in .NET](https://learn.microsoft.com/dotnet/core/extensions/configuration)
- [Options pattern in .NET](https://learn.microsoft.com/dotnet/core/extensions/options)
- [Safe storage of app secrets in development](https://learn.microsoft.com/aspnet/core/security/app-secrets?view=aspnetcore-10.0)
- [Custom configuration providers](https://learn.microsoft.com/dotnet/core/extensions/custom-configuration-provider)
