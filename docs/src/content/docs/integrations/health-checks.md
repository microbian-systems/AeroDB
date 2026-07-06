---
title: Health Checks
description: Health check endpoints for AeroDB
---

# Health Checks

AeroDB provides built-in health checks for SurrealDB connectivity that integrate directly into the ASP.NET Core health check pipeline.

## Built-in SurrealDB Health Check

The `AeroDB.Extensions.Diagnostics.HealthChecks` package adds a health probe that verifies SurrealDB is reachable by executing a lightweight ping query:

```shell
dotnet add package AeroDB.Extensions.Diagnostics.HealthChecks
```

### Default Registration

```csharp
builder.Services
    .AddHealthChecks()
    .AddAeroDb(options =>
    {
        options.ConnectionString = "ws://localhost:8000";
        options.Namespace = "aero";
        options.Database = "healthcheck";
        options.Timeout = TimeSpan.FromSeconds(5);
    });
```

The health check executes `SELECT 1` against the configured namespace/database. A successful response reports **Healthy**; a timeout or connection error reports **Unhealthy**.

## Adding to the ASP.NET Core Pipeline

```csharp
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = _ => true,  // all registered checks
    ResponseWriter = WriteJsonResponse
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("liveness")
});
```

## Readiness vs. Liveness Probes

Tag checks appropriately for Kubernetes-style probe separation:

```csharp
builder.Services
    .AddHealthChecks()
    .AddAeroDb("ws://localhost:8000", "aero", "app", tags: new[] { "readyness" });
```

- **Liveness** (`/health/live`) — lightweight check that the process is alive (no DB dependency).
- **Readiness** (`/health/ready`) — full check including SurrealDB connectivity. The service won't receive traffic until the database is reachable.

## Custom Health Check Configuration

```csharp
services.AddHealthChecks()
    .AddAeroDb(options =>
    {
        options.ConnectionString = builder.Configuration["SurrealDB:Endpoint"];
        options.Namespace = builder.Configuration["SurrealDB:Namespace"];
        options.Database = builder.Configuration["SurrealDB:Database"];
        options.Timeout = TimeSpan.FromSeconds(3);
        options.FailureStatus = HealthStatus.Degraded; // don't kill the pod on slow DB
    }, name: "surrealdb-primary", tags: new[] { "readyness", "database" });
```

## Kubernetes Deployment Example

```yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: app
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 15
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
```

## Dashboard Integration

Combine with [AspNetCore.HealthChecks.UI](https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks) for a real-time dashboard:

```csharp
builder.Services
    .AddHealthChecksUI(setup =>
    {
        setup.AddHealthCheckEndpoint("aero", "/health/ready");
    })
    .AddInMemoryStorage();

app.MapHealthChecksUI(options =>
{
    options.UIPath = "/health/ui";
});
```

The dashboard displays SurrealDB status alongside all other registered health checks, with history and failure notifications.

## See Also

- [Configuration](/docs/configuration)
- [Getting Started](/docs/getting-started)
