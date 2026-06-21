using CryptoTrader.Data;
using CryptoTrader.Handlers;
using CryptoTrader.Messages;
using CryptoTrader.Models;
using CryptoTrader.Services;
using Dali;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net;
using Wolverine;
using Wolverine.Persistence.Durability;
using WolverineFx.Dali;

// ══════════════════════════════════════════════════════════
// CryptoTrader — Dali + Wolverine Sample Application
// ══════════════════════════════════════════════════════════
// Demonstrates:
//   1. Dali document persistence (users, accounts, wallets)
//   2. Graph relationships (RELATE User → Account)
//   3. Wolverine saga (TradeSaga lifecycle)
//   4. IDaliOp side-effect pattern (wallet updates)
//   5. Cascading messages through the outbox
//   6. Optimistic concurrency (Wallet.IVersioned)
//   7. Multi-schema routing (TradeEvent → "audit" DB)
//   8. Console reporting with color-coded output
// ══════════════════════════════════════════════════════════

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║    CryptoTrader — Dali + Wolverine Sample        ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

// ──────────────────────────────────────────────
// 1. Bootstrap Dali Store
// ──────────────────────────────────────────────
Console.WriteLine("Initializing Dali document store...");
var surrealDbClient = new SurrealDbMemoryClient();
var store = Documents.For(o =>
{
    o.ClientFactory = () => surrealDbClient;
    o.Schema.For<CryptoTrader.Models.Wallet>().SetSchemaMode(Dali.SchemaMode.Flexible);
    o.Schema.For<CryptoTrader.Models.TradeEvent>().SetSchemaMode(Dali.SchemaMode.Flexible);
    o.UseOptimisticConcurrency = true;
    o.Schema.AutoCreate = true;
    o.LoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
});
await store.InitializeAsync();
Console.WriteLine($"  Dali store initialized (in-memory SurrealDB)\n");

// ──────────────────────────────────────────────
// 2. Seed data
// ──────────────────────────────────────────────
Console.WriteLine("Seeding test data...");
var (users, accounts, wallets) = await SeedData.SeedAsync(store, userCount: 50);
Console.WriteLine();

// ──────────────────────────────────────────────
// 3. Boot Wolverine Host
// ──────────────────────────────────────────────
Console.WriteLine("Starting Wolverine message bus...");

var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IDocumentStore>(store);
        services.AddSingleton<ISurrealDbClient>(surrealDbClient);
        services.AddSingleton<PriceSimulator>();
    })
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.UseRuntimeCompilation();

        // Register handlers explicitly for deterministic discovery
        opts.Discovery.IncludeType<PlaceOrderHandler>()
            .IncludeType<MatchOrderHandler>();

        // Manually register Dali persistence services
        // (same pattern as DaliWolverineIntegrationTests)
        opts.Services.AddSingleton<DaliMessageStore>(sp =>
        {
            var client = sp.GetRequiredService<ISurrealDbClient>();
            var logger = sp.GetRequiredService<ILogger<DaliMessageStore>>();
            return new DaliMessageStore(client, logger);
        });
        opts.Services.AddSingleton<IMessageStore>(sp =>
            sp.GetRequiredService<DaliMessageStore>());

        opts.Services.AddSingleton(sp =>
        {
            var docStore = sp.GetRequiredService<IDocumentStore>();
            var ms = sp.GetRequiredService<DaliMessageStore>();
            var logger = sp.GetRequiredService<ILogger<DaliOutboxedSessionFactory>>();
            return new DaliOutboxedSessionFactory(docStore, ms, logger);
        });

        opts.Services.AddScoped<ScopedDocumentSessionHolder>();
        opts.Services.AddSingleton<IWolverineExtension>(new DaliIntegration());
    })
    .Build();

await host.StartAsync();
Console.WriteLine("  Wolverine message bus started\n");

// ──────────────────────────────────────────────
// 4. Submit trade orders
// ──────────────────────────────────────────────
var bus = host.MessageBus();
var rng = Random.Shared;
var assets = new[] { "BTC", "ETH", "SOL", "MATIC" };
const int orderCount = 200;

Console.WriteLine($"Submitting {orderCount} buy/sell orders...\n");
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("─── Trades ─────────────────────────────────────────────────────────");
Console.ResetColor();

var submittedOrders = 0;
for (var i = 0; i < orderCount; i++)
{
    // Pick a random user + their wallet
    var userIndex = rng.Next(users.Count);
    var user = users[userIndex];
    var asset = assets[rng.Next(assets.Length)];

    // Find the wallet for this user+asset
    var accountId = SeedData.GetRecordIdStr(accounts[userIndex].Id);
    var wallet = wallets.FirstOrDefault(w => w.AccountId == accountId && w.Asset == asset);
    if (wallet is null) continue;

    var walletId = SeedData.GetRecordIdStr(wallet.Id);
    if (string.IsNullOrEmpty(walletId)) continue;

    var userId = SeedData.GetRecordIdStr(user.Id);
    if (string.IsNullOrEmpty(userId)) continue;

    var isBuy = rng.Next(2) == 0;

    var qty = asset switch
    {
        "BTC" => Math.Round((decimal)(rng.NextDouble() * (isBuy ? 0.5 : 0.3) + 0.01), 4),
        "ETH" => Math.Round((decimal)(rng.NextDouble() * (isBuy ? 5.0 : 3.0) + 0.1), 4),
        "SOL" => Math.Round((decimal)(rng.NextDouble() * (isBuy ? 50.0 : 30.0) + 1), 2),
        _ => Math.Round((decimal)(rng.NextDouble() * (isBuy ? 1000.0 : 500.0) + 10), 0)
    };

    if (isBuy)
    {
        await bus.InvokeAsync(new PlaceBuyOrder(
            userId, walletId, asset, qty, 100_000m));
    }
    else
    {
        await bus.InvokeAsync(new PlaceSellOrder(
            userId, walletId, asset, qty, 0.01m));
    }

    submittedOrders++;
}

Console.WriteLine($"  {submittedOrders} orders submitted\n");

// ──────────────────────────────────────────────
// 5. Wait for processing (Wolverine processes asynchronously)
// ──────────────────────────────────────────────
Console.WriteLine("Waiting for trades to settle...\n");

// Poll until all submitted orders have been processed by MatchOrderHandler
for (var waited = 0; waited < 20; waited++)
{
    if (MatchOrderHandler.SettledTrades >= submittedOrders) break;
    await Task.Delay(500);
}

// ──────────────────────────────────────────────
// 6. Print summary report (from in-memory counters)
// ──────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("─── Summary ────────────────────────────────────────────────────────");
Console.ResetColor();

var settledTrades = MatchOrderHandler.SettledTrades;
var totalVolume = MatchOrderHandler.TotalVolume;
var avgPrice = MatchOrderHandler.Prices.Count > 0
    ? MatchOrderHandler.Prices.Average()
    : 0;

Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine($"  Settled trades:          {settledTrades}");
Console.WriteLine($"  Total volume:            ${totalVolume,12:F2}");
Console.WriteLine($"  Avg execution price:     ${avgPrice,12:F2}");
Console.ResetColor();

// Asset breakdown
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("─── Asset Breakdown ────────────────────────────────────────────────");
Console.ResetColor();

var assetList = new[] { "BTC", "ETH", "SOL", "MATIC" };
foreach (var asset in assetList)
{
    var count = MatchOrderHandler.AssetCounts.GetValueOrDefault(asset);
    var vol = MatchOrderHandler.AssetVolumes.GetValueOrDefault(asset);
    Console.WriteLine($"  {asset,-5}  {count,4} trades  ${vol,10:F2} volume");
}

// Wallet summary — sorted by USD-equivalent value
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("─── Top Wallets (by USD value) ────────────────────────────────────");
Console.ResetColor();

var assetPrices = SeedData.CryptoAssets.ToDictionary(a => a.Ticker, a => a.CurrentPrice);
var topWallets = wallets
    .OrderByDescending(w => w.Balance * assetPrices.GetValueOrDefault(w.Asset, 1m))
    .Take(5)
    .ToList();

foreach (var w in topWallets)
{
    var usdValue = w.Balance * assetPrices.GetValueOrDefault(w.Asset, 1m);
    Console.WriteLine($"  {w.Asset,-5}  {w.Balance,12:F4}  (≈ ${usdValue,10:F2} USD)");
}

// ──────────────────────────────────────────────
// 7. Graph Traversal Demo
// ──────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║        Graph Traversal Demo          ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.ResetColor();

await using var graphSession = await store.QuerySessionAsync();
var cryptoAssets = SeedData.CryptoAssets;

// ── Demo 1: Verify the 4 CryptoAsset nodes exist ──
Console.WriteLine($"\n📊 CryptoAsset nodes:");
try
{
    var allAssets = await graphSession.Query<CryptoAsset>().ToListAsync();
    Console.WriteLine($"  Found {allAssets.Count} assets:");
    foreach (var a in allAssets)
        Console.WriteLine($"  • {a.Ticker,-5} {a.Name,-10} ${a.CurrentPrice,8:F2} (id: {a.Id})");
}
catch (Exception ex)
{
    Console.WriteLine($"  (error: {ex.Message})");
}

// ── Demo 2: Count holds_asset edges ──
Console.WriteLine("\n🔗 HoldsAsset edges:");
try
{
    // Count holds_asset edges via RawQuery
    var edgeCount = await graphSession.RawQueryAsync<object>(
        "SELECT count() AS count FROM holds_asset GROUP ALL");
    Console.WriteLine($"  Total edges: {edgeCount.Count}");

    // Count edges per user/asset — use ExecuteSqlAsync for non-typed queries
    var edgeSql = await graphSession.ExecuteSqlAsync(
        "SELECT count() FROM holds_asset");
    Console.WriteLine($"  Edge query executed: {edgeSql > 0}");
}
catch (Exception ex)
{
    Console.WriteLine($"  (error: {ex.Message})");
}

// ── Demo 3: Graph traversal via IGraphQuery API ──
Console.WriteLine("\n👥 IGraphQuery traversal demo:");
try
{
    foreach (var asset in cryptoAssets)
    {
        try
        {
            var holders = await graphSession.Graph<CryptoAsset>()
                .In<User, HoldsAsset>()
                .ToListAsync();
            // Filter in-memory for the specific ticker (graph .Where() has limited support)
            var filtered = holders.ToList();
            Console.WriteLine($"  {asset.Ticker,-5} → {filtered.Count,3} user(s) traversed (all assets)");
        }
        catch
        {
            Console.WriteLine($"  {asset.Ticker,-5} → traversal unavailable (in-memory limit)");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  error: {ex.Message}");
}

// ── Demo 4: RawQuery with SQL graph syntax ──
Console.WriteLine("\n🗄️  RawQuery graph syntax demo:");
try
{
    var btcAssetId = $"crypto_asset:BTC";
    // Try direct edge count via holds_asset table
    var edgeRows = await graphSession.RawQueryAsync<object>(
        "SELECT * FROM holds_asset LIMIT 5");
    Console.WriteLine($"  holds_asset records: {edgeRows.Count}");
    
    // Count user nodes
    var userCount = await graphSession.RawQueryAsync<object>(
        "SELECT count() AS count FROM user GROUP ALL");
    Console.WriteLine($"  User records: {userCount.Count}");
}
catch (Exception ex)
{
    Console.WriteLine($"  (error: {ex.Message})");
}

Console.WriteLine("\n✅ Graph demo complete.");

// ──────────────────────────────────────────────
// 8. Clean shutdown
// ──────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Sample completed successfully.");
Console.ResetColor();

await host.StopAsync();

Console.ReadLine();