using AeroDB;
using AeroDB.Samples.Shared;
using SurrealDb.Embedded.InMemory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Create AeroDB store and register as singleton
var store = Documents.For(o =>
{
    o.ClientFactory = () => new SurrealDbMemoryClient();
    o.Namespace = "cli";
    o.Database = "cli";
    o.Schema.For<User>().Identity(x => x.Id);
    o.Schema.For<Target>().Identity(x => x.Id);
    o.Schema.For<Target>().SoftDeleted = true;

    // Register all event store projections ahead of time
    o.Projections.Add(new TripProjectionWithCustomName(), ProjectionLifecycle.Async);
    o.Projections.Add(new DayProjection(), ProjectionLifecycle.Async);
    o.Projections.Add(new DistanceProjection(), ProjectionLifecycle.Async);
});
await store.InitializeAsync();
builder.Services.AddSingleton<IDocumentStore>(store);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

await app.RunAsync();
