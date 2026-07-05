using AeroDB;
using Microsoft.AspNetCore.Identity;
using SurrealDb.Net;

var builder = WebApplication.CreateBuilder(args);

// ── AeroDB Document Store ────────────────────────────────────────
builder.Services.AddAeroDB(o =>
{
    //o.ClientFactory = () => new SurrealDbMemoryClient();
    o.ClientFactory = () => new SurrealDbClient("ws://localhost:8000/rpc");
    o.Namespace = "identity";
    o.Database = "identity";
});

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddRoles<IdentityRole>()
    .AddAeroDBStores<IdentityUser, IdentityRole>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
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
