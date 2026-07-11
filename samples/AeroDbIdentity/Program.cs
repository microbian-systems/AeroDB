using AeroDB.Sable;
using Microsoft.AspNetCore.Identity;
using SurrealDb.Net;

var builder = WebApplication.CreateBuilder(args);

// ── AeroDB.Sable Document Store ────────────────────────────────────────
builder.Services.AddAeroDB(o =>
{
    //o.ClientFactory = () => new SurrealDbMemoryClient();
    o.ClientFactory = () => new SurrealDbClient("ws://localhost:8000/rpc");
    o.Namespace = "aero";
    o.Database = "identity";
});

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddRoles<IdentityRole>()
    .AddAeroDBStores<IdentityUser, IdentityRole>();
builder.Services.AddRazorPages();

// ── Passkey Configuration ────────────────────────────────────────
builder.Services.Configure<IdentityPasskeyOptions>(options =>
{
    // options.ServerDomain = "your-domain.com";
    options.UserVerificationRequirement = "preferred";
    options.ResidentKeyRequirement = "preferred";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// ── Passkey API Endpoints ───────────────────────────────────────
var passkeyApi = app.MapGroup("/api/passkeys");

// Registration: Get creation options for authenticated user
passkeyApi.MapPost("/creation-options", async (
    HttpContext context,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager) =>
{
    var user = await userManager.GetUserAsync(context.User);
    if (user is null) return Results.Unauthorized();

    var userId = await userManager.GetUserIdAsync(user);
    var userName = await userManager.GetUserNameAsync(user) ?? "User";

    var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
    {
        Id = userId,
        Name = userName,
        DisplayName = userName
    });

    return Results.Content(optionsJson, "application/json");
}).RequireAuthorization();

// Registration: Verify attestation and store the passkey
passkeyApi.MapPost("/attestation", async (
    HttpContext context,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    PasskeyAttestationRequest request) =>
{
    var user = await userManager.GetUserAsync(context.User);
    if (user is null) return Results.Unauthorized();

    var attestationResult = await signInManager.PerformPasskeyAttestationAsync(request.CredentialJson);
    if (!attestationResult.Succeeded)
    {
        return Results.BadRequest(new { error = attestationResult.Failure?.Message ?? "Attestation failed" });
    }

    var passkey = attestationResult.Passkey;
    if (!string.IsNullOrWhiteSpace(request.Name))
    {
        passkey.Name = request.Name;
    }

    var addResult = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
    if (!addResult.Succeeded)
    {
        return Results.BadRequest(new { error = "Failed to store passkey" });
    }

    return Results.Ok();
}).RequireAuthorization();

// Authentication: Get assertion request options (public endpoint)
passkeyApi.MapPost("/request-options", async (
    SignInManager<IdentityUser> signInManager,
    UserManager<IdentityUser> userManager,
    string? username) =>
{
    IdentityUser? user = null;
    if (!string.IsNullOrEmpty(username))
    {
        user = await userManager.FindByNameAsync(username);
    }

    var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user);
    return Results.Content(optionsJson, "application/json");
});

// Authentication: Verify assertion and sign in (public endpoint)
passkeyApi.MapPost("/sign-in", async (
    SignInManager<IdentityUser> signInManager,
    PasskeySignInRequest request) =>
{
    var result = await signInManager.PasskeySignInAsync(request.CredentialJson);

    if (result.Succeeded)
    {
        return Results.Ok();
    }

    if (result.IsLockedOut)
    {
        return Results.BadRequest(new { error = "Account is locked out" });
    }

    return Results.Unauthorized();
});

// Management: List user's passkeys
passkeyApi.MapGet("/", async (
    HttpContext context,
    UserManager<IdentityUser> userManager) =>
{
    var user = await userManager.GetUserAsync(context.User);
    if (user is null) return Results.Unauthorized();

    var passkeys = await userManager.GetPasskeysAsync(user);
    var result = passkeys.Select(p => new PasskeyItem(
        CredentialId: Convert.ToBase64String(p.CredentialId),
        Name: p.Name ?? "Unnamed passkey",
        CreatedAt: p.CreatedAt,
        IsBackedUp: p.IsBackedUp
    ));

    return Results.Ok(result);
}).RequireAuthorization();

// Management: Remove a passkey
passkeyApi.MapDelete("/{credentialId}", async (
    HttpContext context,
    UserManager<IdentityUser> userManager,
    string credentialId) =>
{
    var user = await userManager.GetUserAsync(context.User);
    if (user is null) return Results.Unauthorized();

    var credentialBytes = Convert.FromBase64String(credentialId);
    var result = await userManager.RemovePasskeyAsync(user, credentialBytes);

    return result.Succeeded
        ? Results.Ok()
        : Results.BadRequest(new { error = "Failed to remove passkey" });
}).RequireAuthorization();

// Management: Rename a passkey
passkeyApi.MapPut("/{credentialId}/name", async (
    HttpContext context,
    UserManager<IdentityUser> userManager,
    string credentialId,
    PasskeyRenameRequest request) =>
{
    var user = await userManager.GetUserAsync(context.User);
    if (user is null) return Results.Unauthorized();

    var credentialBytes = Convert.FromBase64String(credentialId);
    var passkeys = await userManager.GetPasskeysAsync(user);
    var passkey = passkeys.FirstOrDefault(p => p.CredentialId.SequenceEqual(credentialBytes));

    if (passkey is null)
    {
        return Results.NotFound(new { error = "Passkey not found" });
    }

    passkey.Name = request.Name;
    var updateResult = await userManager.AddOrUpdatePasskeyAsync(user, passkey);

    return updateResult.Succeeded
        ? Results.Ok()
        : Results.BadRequest(new { error = "Failed to rename passkey" });
}).RequireAuthorization();

app.Run();

// ── Request / Response DTOs ──────────────────────────────────────
public record PasskeyAttestationRequest(string CredentialJson, string? Name);
public record PasskeySignInRequest(string CredentialJson);
public record PasskeyRenameRequest(string Name);
public record PasskeyItem(string CredentialId, string Name, DateTimeOffset CreatedAt, bool IsBackedUp);
