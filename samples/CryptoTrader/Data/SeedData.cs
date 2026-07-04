using Bogus;
using CryptoTrader.Models;
using Dali;
using SurrealDb.Net.Models;

namespace CryptoTrader.Data;

/// <summary>
/// Generates realistic test data using Bogus.
/// Demonstrates:
/// - Dali document CRUD (Store + SaveChangesAsync)
/// - Graph relationships via Relate (User → Account, User → holds_asset → CryptoAsset), flushed in batches with SaveChangesAsync
/// </summary>
public static class SeedData
{
    private static readonly string[] Assets = ["BTC", "ETH", "SOL", "MATIC"];

    /// <summary>
    /// Returns the seeded CryptoAsset records so Program.cs can use them for graph traversal.
    /// </summary>
    public static List<CryptoAsset> CryptoAssets { get; } = new()
    {
        new()
        {
            Id = new RecordIdOf<string>("crypto_asset", "BTC"),
            Ticker = "BTC",
            Name = "Bitcoin",
            CurrentPrice = 68420m
        },
        new()
        {
            Id = new RecordIdOf<string>("crypto_asset", "ETH"),
            Ticker = "ETH",
            Name = "Ethereum",
            CurrentPrice = 3450m
        },
        new()
        {
            Id = new RecordIdOf<string>("crypto_asset", "SOL"),
            Ticker = "SOL",
            Name = "Solana",
            CurrentPrice = 145.30m
        },
        new()
        {
            Id = new RecordIdOf<string>("crypto_asset", "MATIC"),
            Ticker = "MATIC",
            Name = "Polygon",
            CurrentPrice = 0.72m
        }
    };

    public static async Task<(List<User> Users, List<Account> Accounts, List<Wallet> Wallets)> SeedAsync(
        IDocumentStore store,
        int userCount = 50)
    {
        var userFaker = new Faker<User>()
            .RuleFor(u => u.Name, f => f.Person.FullName)
            .RuleFor(u => u.Email, f => f.Person.Email);

        var accountFaker = new Faker<Account>()
            .RuleFor(a => a.Name, f => $"Trading Account {f.Random.AlphaNumeric(6).ToUpperInvariant()}")
            .RuleFor(a => a.UsdBalance, f => Math.Round(f.Finance.Amount(10_000, 500_000), 2));

        var users = new List<User>();
        var accounts = new List<Account>();
        var wallets = new List<Wallet>();

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // 0. Store the 4 crypto asset nodes so they exist for graph references
        foreach (var asset in CryptoAssets)
            session.Store(asset);

        for (var i = 0; i < userCount; i++)
        {
            var userId = $"{Guid.NewGuid():N}";
            var accountId = $"{Guid.NewGuid():N}";

            // 1. Create User with explicit ID
            var user = userFaker.Generate();
            user.Id = new RecordIdOf<string>("user", userId);
            session.Store(user);

            // 2. Create Account with explicit ID
            var account = accountFaker.Generate();
            account.Id = new RecordIdOf<string>("account", accountId);
            session.Store(account);

            // 3. Create Wallets (one per asset) with explicit IDs
            foreach (var asset in Assets)
            {
                var walletId = $"{Guid.NewGuid():N}";
                var wallet = new Wallet
                {
                    Id = new RecordIdOf<string>("wallet", walletId),
                    Asset = asset,
                    Balance = Math.Round(asset switch
                    {
                        "BTC" => (decimal)Random.Shared.NextDouble() * 2m,
                        "ETH" => (decimal)Random.Shared.NextDouble() * 30m,
                        "SOL" => (decimal)Random.Shared.NextDouble() * 500m,
                        _ => (decimal)Random.Shared.NextDouble() * 10_000m
                    }, 4),
                    Reserved = 0,
                    AccountId = accountId,
                    Version = 1
                };
                session.Store(wallet);
                wallets.Add(wallet);
            }

            users.Add(user);
            accounts.Add(account);
        }

        await session.SaveChangesAsync();

        // 4. Create graph relationships: User → owns_account → Account
        foreach (var (user, account) in users.Zip(accounts))
        {
            if (user.Id is null || account.Id is null) continue;

            session.Relate<OwnsAccount>(
                user.Id,
                account.Id,
                data: new OwnsAccount { Type = "owns_account" }
            );
        }

        await session.SaveChangesAsync();

        // 5. Create graph relationships: User → holds_asset → CryptoAsset
        // Each user holds 1-3 random crypto assets
        foreach (var user in users)
        {
            if (user.Id is null) continue;

            var assetCount = Random.Shared.Next(1, 4);
            var selected = CryptoAssets
                .OrderBy(_ => Random.Shared.Next())
                .Take(assetCount)
                .ToList();

            foreach (var asset in selected)
            {
                if (asset.Id is null) continue;

                session.Relate<HoldsAsset>(
                    user.Id,
                    asset.Id,
                    data: new HoldsAsset
                    {
                        AveragePrice = asset.CurrentPrice,
                        TotalQuantity = Math.Round((decimal)(Random.Shared.NextDouble() * 10 + 0.1), 4),
                        FirstAcquired = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 365))
                    }
                );
            }
        }

        await session.SaveChangesAsync();

        Console.WriteLine($"  ✓ {users.Count} users, {accounts.Count} accounts, {wallets.Count} wallets, {CryptoAssets.Count} assets");
        return (users, accounts, wallets);
    }

    /// <summary>Extracts the string ID from a RecordId.</summary>
    public static string GetRecordIdStr(RecordId? id)
        => id switch
        {
            RecordIdOf<string> s => s.Id,
            RecordIdOf<long> l => l.Id.ToString(),
            RecordIdOf<int> i => i.Id.ToString(),
            _ => id?.ToString() ?? string.Empty
        };
}

/// <summary>Edge record for User → owns_account → Account relationship.</summary>
public class OwnsAccount : EdgeRecord
{
    public string Type { get; set; } = string.Empty;
}
