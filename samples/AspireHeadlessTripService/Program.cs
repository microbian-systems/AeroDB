using AeroDB;
using AeroDB.Sable;
using AeroDB.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using SurrealDb.Embedded.SurrealKv;

var builder = Host.CreateApplicationBuilder();

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

#region sample_enabling_open_telemetry_exporting_from_marten

// This is passed in by Project Aspire. The exporter usage is a little
// different for other tools like Prometheus or SigNoz
var endpointUri = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
Console.WriteLine("OLTP endpoint: " + endpointUri);

builder.Services.AddOpenTelemetry().UseOtlpExporter();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("AeroDB");
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("AeroDB");
    });

#endregion

// Create AeroDB store and register
var store = Documents.For(opts =>
{
    opts.ClientFactory = () => new SurrealDbKvClient("aspiresservice");
    opts.Namespace = "cli";
    opts.Database = "cli";

    // Multi-tenancy: Conjoined (tenant_id field within a single database)
    opts.TenancyStyle = TenancyStyle.Conjoined;

    // Enable event sourcing for projections
    opts.Events.Enabled = true;

    // Register all event store projections ahead of time
    opts.Projections
        .Add(new TripProjectionWithCustomName(), ProjectionLifecycle.Async);

    opts.Projections
        .Add(new DayProjection(), ProjectionLifecycle.Async);

    opts.Projections
        .Add(new DistanceProjection(), ProjectionLifecycle.Async);
});
await store.InitializeAsync();

builder.Services.AddSingleton<IDocumentStore>(store);

// Manually register the projection coordinator for daemon lifecycle management
builder.Services.AddSingleton<IProjectionCoordinator>(sp =>
    new ProjectionCoordinator(sp.GetRequiredService<IDocumentStore>()));
builder.Services.AddSingleton<IHostedService>(sp =>
    sp.GetRequiredService<IProjectionCoordinator>());

await builder.Build().RunAsync();
