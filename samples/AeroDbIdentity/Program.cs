using AeroDB;
using Microsoft.AspNetCore.Identity;
using SurrealDb.Embedded.InMemory;
using AeroDbIdentity.Data;

var builder = WebApplication.CreateBuilder(args);

// ── AeroDB Document Store ────────────────────────────────────────
var store = Documents.For(o =>
{
    o.ClientFactory = () => new SurrealDbMemoryClient();
    o.Namespace = "identity";
    o.Database = "identity";
});
await store.InitializeAsync();
builder.Services.AddSingleton<IDocumentStore>(store);

builder.Services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddRoles<IdentityRole>()
    .AddAeroDBStores<ApplicationUser, IdentityRole>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
