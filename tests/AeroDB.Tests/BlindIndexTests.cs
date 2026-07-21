using System.Security.Cryptography;
using AeroDB.Sable;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Tests;

public sealed class BlindIndexTests
{
    [Test]
    public void Us_ssn_normalization_is_stable_and_rejects_malformed_values()
    {
        var formatted = BlindIndexNormalization.Normalize(
            "123-45-6789",
            BlindIndexNormalizer.UsSocialSecurityNumberV1);
        var compact = BlindIndexNormalization.Normalize(
            "123456789",
            BlindIndexNormalizer.UsSocialSecurityNumberV1);

        formatted.ShouldBe(compact);
        Should.Throw<SableBlindIndexNormalizationException>(() =>
            BlindIndexNormalization.Normalize(
                "123-45-678",
                BlindIndexNormalizer.UsSocialSecurityNumberV1));
        Should.Throw<SableBlindIndexNormalizationException>(() =>
            BlindIndexNormalization.Normalize(
                "123-AB-6789",
                BlindIndexNormalizer.UsSocialSecurityNumberV1));
    }

    [Test]
    public async Task Tokens_are_stable_within_scope_and_change_across_scopes()
    {
        using var provider = new HmacSha256BlindIndexProvider(
            "bidx-current",
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var normalized = BlindIndexNormalization.Normalize(
            "123-45-6789",
            BlindIndexNormalizer.UsSocialSecurityNumberV1);
        try
        {
            var context = new BlindIndexContext(
                "aero",
                "customers",
                "customer",
                "tenant-a",
                "social_security_number_bidx",
                BlindIndexNormalizer.UsSocialSecurityNumberV1);
            var first = await provider.CreateStorageTokenAsync(
                normalized,
                context,
                BlindIndexAlgorithm.HmacSha256);
            var second = await provider.CreateStorageTokenAsync(
                normalized,
                context,
                BlindIndexAlgorithm.HmacSha256);
            var otherTenant = await provider.CreateStorageTokenAsync(
                normalized,
                context with { TenantId = "tenant-b" },
                BlindIndexAlgorithm.HmacSha256);
            var otherField = await provider.CreateStorageTokenAsync(
                normalized,
                context with { StorageField = "other_bidx" },
                BlindIndexAlgorithm.HmacSha256);

            first.ShouldBe(second);
            first.ShouldBe(
                "bidx:v1:YmlkeC1jdXJyZW50:" +
                "galH2jebY0xA_Ru4IS7eNL1zyXUWKrD9S9ckydsDRfg");
            first.ShouldStartWith("bidx:v1:");
            otherTenant.ShouldNotBe(first);
            otherField.ShouldNotBe(first);
            first.ShouldNotContain("123456789");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalized);
        }
    }

    [Test]
    public async Task Rotation_queries_active_and_previous_versions_but_stores_only_active()
    {
        var previous = new Dictionary<string, byte[]>
        {
            ["bidx-previous"] = RandomNumberGenerator.GetBytes(32)
        };
        using var provider = new HmacSha256BlindIndexProvider(
            "bidx-current",
            RandomNumberGenerator.GetBytes(32),
            previous);
        var normalized = BlindIndexNormalization.Normalize(
            "123456789",
            BlindIndexNormalizer.UsSocialSecurityNumberV1);
        try
        {
            var context = new BlindIndexContext(
                "aero",
                "customers",
                "customer",
                null,
                "social_security_number_bidx",
                BlindIndexNormalizer.UsSocialSecurityNumberV1);
            var storage = await provider.CreateStorageTokenAsync(
                normalized,
                context,
                BlindIndexAlgorithm.HmacSha256);
            var queryTokens = await provider.CreateQueryTokensAsync(
                normalized,
                context,
                BlindIndexAlgorithm.HmacSha256);

            storage.ShouldContain("YmlkeC1jdXJyZW50");
            queryTokens.Count.ShouldBe(2);
            queryTokens.ShouldContain(storage);
            queryTokens.ShouldContain(token => token.Contains("YmlkeC1wcmV2aW91cw"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalized);
        }
    }

    [Test]
    public async Task Save_writes_only_ciphertext_and_a_versioned_blind_token()
    {
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = blindIndexes;
            options.Schema.For<BlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new BlindIndexedCustomer
        {
            Id = "customer-1",
            Name = "Ada",
            SocialSecurityNumber = "123-45-6789"
        });

        await session.SaveChangesAsync();

        var stored = await session.RawQueryAsync<BlindIndexStorageProbe>(
            "SELECT social_security_number.ciphertext AS ciphertext, " +
            "social_security_number_bidx AS blind_index " +
            "FROM blind_indexed_customer:`customer-1`;");

        stored.Count.ShouldBe(1);
        stored[0].Ciphertext.ShouldNotBeNullOrWhiteSpace();
        stored[0].Ciphertext.ShouldNotContain("123-45-6789");
        stored[0].Ciphertext.ShouldNotContain("123456789");
        stored[0].BlindIndex.ShouldStartWith("bidx:v1:");
        stored[0].BlindIndex.ShouldNotContain("123456789");
    }

    [Test]
    public async Task Lookup_sends_only_versioned_tokens_as_bound_parameters()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));

        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        var options = CreateOptions(wrapping, blindIndexes);
        var session = new DocumentSession(
            client,
            surrealSession,
            options,
            DocumentTracking.None);

        var results = await session.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
            customer => customer.SocialSecurityNumber,
            "123-45-6789");

        results.ShouldBeEmpty();
        await surrealSession.Received(1).RawQuery(
            Arg.Is<string>(surql =>
                surql.Contains("social_security_number_bidx = $blind_index_0", StringComparison.Ordinal)
                && !surql.Contains("123-45-6789", StringComparison.Ordinal)
                && !surql.Contains("123456789", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(
                parameters => HasSafeBlindIndexParameter(parameters)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Verification_fails_closed_when_a_hit_does_not_match_decrypted_plaintext()
    {
        var descriptor = new AeroDB.Sable.Metadata.BlindIndexDescriptor(
            nameof(BlindIndexedCustomer.SocialSecurityNumber),
            BlindIndexAlgorithm.HmacSha256,
            BlindIndexNormalizer.UsSocialSecurityNumberV1,
            null,
            value => ((BlindIndexedCustomer)value).SocialSecurityNumber);
        var normalized = BlindIndexNormalization.Normalize(
            "123-45-6789",
            BlindIndexNormalizer.UsSocialSecurityNumberV1);
        try
        {
            Should.Throw<SableBlindIndexIntegrityException>(() =>
                BlindIndexQueryExtensions.VerifyResults(
                [
                new BlindIndexedCustomer
                {
                    Id = "corrupt",
                    SocialSecurityNumber = "999-99-9999"
                }
                ],
                descriptor,
                normalized));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalized);
        }
    }

    [Test]
    public async Task Lookup_rejects_non_sable_query_session_implementations()
    {
        var session = Substitute.For<IQuerySession>();

        await Should.ThrowAsync<SableBlindIndexConfigurationException>(() =>
            session.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
                customer => customer.SocialSecurityNumber,
                "123-45-6789"));
        await session.DidNotReceiveWithAnyArgs()
            .RawQueryAsync<BlindIndexedCustomer>(default!, default, default);
    }

    [Test]
    public async Task Lookup_applies_bound_tenant_and_soft_delete_filters()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        var options = new StoreOptions { TenancyStyle = TenancyStyle.Conjoined };
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Encryption.BlindIndexProvider = blindIndexes;
        options.Schema.For<TenantSoftDeletedCustomer>()
            .Identity(customer => customer.Id)
            .MultiTenanted()
            .SoftDeleted()
            .EncryptField(customer => customer.SocialSecurityNumber)
            .BlindIndex(customer => customer.SocialSecurityNumber);
        var session = new DocumentSession(
            client,
            surrealSession,
            options,
            DocumentTracking.None);
        session.SetTenant("tenant-a");

        await session.WhereEncryptedEqualsAsync<TenantSoftDeletedCustomer>(
            customer => customer.SocialSecurityNumber,
            "123-45-6789");

        await surrealSession.Received(1).RawQuery(
            Arg.Is<string>(surql =>
                surql.Contains("tenant_id = $blind_index_tenant", StringComparison.Ordinal)
                && surql.Contains("deleted = false", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters =>
                parameters != null
                && parameters.ContainsKey("blind_index_tenant")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Fluent_blind_index_round_trips_and_finds_an_ssn()
    {
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = blindIndexes;
            options.Schema.For<BlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new BlindIndexedCustomer
            {
                Id = "customer-lookup",
                Name = "Grace",
                SocialSecurityNumber = "123-45-6789"
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        var matches = await read.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
            customer => customer.SocialSecurityNumber,
            "123456789");

        var customer = matches.ShouldHaveSingleItem();
        customer.Name.ShouldBe("Grace");
        customer.SocialSecurityNumber.ShouldBe("123-45-6789");
    }

    [Test]
    public async Task Updating_an_encrypted_value_replaces_the_old_blind_index_token()
    {
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = blindIndexes;
            options.Schema.For<BlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new BlindIndexedCustomer
            {
                Id = "customer-update",
                SocialSecurityNumber = "123-45-6789"
            });
            await write.SaveChangesAsync();
        }

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new BlindIndexedCustomer
            {
                Id = "customer-update",
                SocialSecurityNumber = "987-65-4321"
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        (await read.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
            customer => customer.SocialSecurityNumber,
            "123-45-6789")).ShouldBeEmpty();
        (await read.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
            customer => customer.SocialSecurityNumber,
            "987-65-4321")).ShouldHaveSingleItem().Id.ShouldBe("customer-update");
    }

    [Test]
    public async Task Attribute_blind_index_round_trips_and_finds_an_ssn()
    {
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = blindIndexes;
            options.Schema.For<AttributedBlindIndexedCustomer>();
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new AttributedBlindIndexedCustomer
            {
                Id = 4441,
                SocialSecurityNumber = "321-54-9876"
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        var matches = await read.WhereEncryptedEqualsAsync<AttributedBlindIndexedCustomer>(
            customer => customer.SocialSecurityNumber,
            "321549876");

        matches.ShouldHaveSingleItem().SocialSecurityNumber.ShouldBe("321-54-9876");
    }

    [Test]
    public async Task Rotation_finds_active_and_previous_rows_until_previous_key_is_retired()
    {
        var previousKey = RandomNumberGenerator.GetBytes(32);
        var activeKey = RandomNumberGenerator.GetBytes(32);
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var previousProvider = new HmacSha256BlindIndexProvider(
            "bidx-previous",
            previousKey);
        using var rotatingProvider = new HmacSha256BlindIndexProvider(
            "bidx-active",
            activeKey,
            new Dictionary<string, byte[]>
            {
                ["bidx-previous"] = previousKey
            });
        using var activeOnlyProvider = new HmacSha256BlindIndexProvider(
            "bidx-active",
            activeKey);
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = previousProvider;
            options.Schema.For<BlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new BlindIndexedCustomer
            {
                Id = "previous-row",
                SocialSecurityNumber = "123-45-6789"
            });
            await write.SaveChangesAsync();
        }

        store.Options.Encryption.BlindIndexProvider = rotatingProvider;
        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new BlindIndexedCustomer
            {
                Id = "active-row",
                SocialSecurityNumber = "123456789"
            });
            await write.SaveChangesAsync();
        }

        await using (var read = await store.QuerySessionAsync())
        {
            var duringRotation = await read.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
                customer => customer.SocialSecurityNumber,
                "123-45-6789");
            duringRotation.Count.ShouldBe(2);
        }

        await using (var maintenance = await store.LightweightSessionAsync())
        {
            var firstPass = await maintenance
                .ReindexBlindIndexesAsync<BlindIndexedCustomer>();
            firstPass.DocumentsScanned.ShouldBe(2);
            firstPass.TokensWritten.ShouldBe(2);

            var secondPass = await maintenance
                .ReindexBlindIndexesAsync<BlindIndexedCustomer>();
            secondPass.ShouldBe(firstPass);
        }

        store.Options.Encryption.BlindIndexProvider = activeOnlyProvider;
        await using (var read = await store.QuerySessionAsync())
        {
            var afterRetirement = await read.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
                customer => customer.SocialSecurityNumber,
                "123-45-6789");
            afterRetirement.Count.ShouldBe(2);
            afterRetirement.Select(customer => customer.Id)
                .ShouldBe(["active-row", "previous-row"], ignoreOrder: true);
        }
    }

    [Test]
    public async Task Reindex_is_bounded_and_never_rewrites_another_tenants_rows()
    {
        var previousKey = RandomNumberGenerator.GetBytes(32);
        var activeKey = RandomNumberGenerator.GetBytes(32);
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "test-kek-v1", "tests");
        using var previousProvider = new HmacSha256BlindIndexProvider(
            "bidx-previous", previousKey);
        using var rotatingProvider = new HmacSha256BlindIndexProvider(
            "bidx-active",
            activeKey,
            new Dictionary<string, byte[]> { ["bidx-previous"] = previousKey });
        using var activeOnlyProvider = new HmacSha256BlindIndexProvider(
            "bidx-active", activeKey);
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = previousProvider;
            options.Schema.For<TenantBlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .MultiTenanted()
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });

        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
        {
            await using var write = await store.LightweightSessionAsync();
            write.SetTenant(tenant);
            write.Store(new TenantBlindIndexedCustomer
            {
                Id = tenant,
                SocialSecurityNumber = "123-45-6789"
            });
            await write.SaveChangesAsync();
        }

        store.Options.Encryption.BlindIndexProvider = rotatingProvider;
        await using (var maintenance = await store.LightweightSessionAsync())
        {
            maintenance.SetTenant("tenant-a");
            var pass = await maintenance
                .ReindexBlindIndexesAsync<TenantBlindIndexedCustomer>(batchSize: 1);
            pass.DocumentsScanned.ShouldBe(1);
            pass.HasMore.ShouldBeFalse();
        }

        store.Options.Encryption.BlindIndexProvider = activeOnlyProvider;
        await using (var tenantA = await store.QuerySessionAsync())
        {
            tenantA.SetTenant("tenant-a");
            (await tenantA.WhereEncryptedEqualsAsync<TenantBlindIndexedCustomer>(
                customer => customer.SocialSecurityNumber,
                "123-45-6789")).ShouldHaveSingleItem().Id.ShouldBe("tenant-a");
        }
        await using (var tenantB = await store.QuerySessionAsync())
        {
            tenantB.SetTenant("tenant-b");
            (await tenantB.WhereEncryptedEqualsAsync<TenantBlindIndexedCustomer>(
                customer => customer.SocialSecurityNumber,
                "123-45-6789")).ShouldBeEmpty();
        }
    }

    [Test]
    public async Task Reindex_detects_a_concurrent_sidecar_update_instead_of_overwriting_it()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "test-kek-v1", "tests");
        using var inner = new HmacSha256BlindIndexProvider("bidx-active", key);
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = inner;
            options.Schema.For<BlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new BlindIndexedCustomer
            {
                Id = "concurrent-row",
                SocialSecurityNumber = "123-45-6789"
            });
            await write.SaveChangesAsync();
        }

        DocumentSession? maintenanceSession = null;
        var callbackProvider = new CallbackBlindIndexProvider(
            inner,
            async cancellationToken =>
            {
                var response = await maintenanceSession!.Session.RawQuery(
                    "UPDATE blind_indexed_customer:`concurrent-row` " +
                    "SET social_security_number_bidx = 'concurrent-change';",
                    null,
                    cancellationToken);
                response.EnsureAllOks();
            });
        store.Options.Encryption.BlindIndexProvider = callbackProvider;

        await using var maintenance =
            (DocumentSession)await store.LightweightSessionAsync();
        maintenanceSession = maintenance;
        await Should.ThrowAsync<SableBlindIndexReindexConflictException>(
            async () => await maintenance.ReindexBlindIndexesAsync<BlindIndexedCustomer>());
    }

    [Test]
    public async Task Reindex_cursor_does_not_skip_after_an_earlier_record_is_deleted()
    {
        var previousKey = RandomNumberGenerator.GetBytes(32);
        var activeKey = RandomNumberGenerator.GetBytes(32);
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "test-kek-v1", "tests");
        using var previousProvider = new HmacSha256BlindIndexProvider(
            "bidx-previous", previousKey);
        using var rotatingProvider = new HmacSha256BlindIndexProvider(
            "bidx-active",
            activeKey,
            new Dictionary<string, byte[]> { ["bidx-previous"] = previousKey });
        using var activeOnlyProvider = new HmacSha256BlindIndexProvider(
            "bidx-active", activeKey);
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Encryption.BlindIndexProvider = previousProvider;
            options.Schema.For<BlindIndexedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .BlindIndex(customer => customer.SocialSecurityNumber);
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            foreach (var id in new[] { "cursor-a", "cursor-b", "cursor-c" })
            {
                write.Store(new BlindIndexedCustomer
                {
                    Id = id,
                    SocialSecurityNumber = "123-45-6789"
                });
            }
            await write.SaveChangesAsync();
        }

        store.Options.Encryption.BlindIndexProvider = rotatingProvider;
        await using (var maintenance = await store.LightweightSessionAsync())
        {
            var first = await maintenance
                .ReindexBlindIndexesAsync<BlindIndexedCustomer>(batchSize: 1);
            first.HasMore.ShouldBeTrue();
            first.NextCursor.ShouldNotBeNull();
            first.NextCursor!.DeserializeId<string>().ShouldBe("cursor-a");

            maintenance.HardDelete<BlindIndexedCustomer>("cursor-a");
            await maintenance.SaveChangesAsync();

            var second = await maintenance.ReindexBlindIndexesAsync<BlindIndexedCustomer>(
                batchSize: 1,
                after: first.NextCursor);
            second.NextCursor!.DeserializeId<string>().ShouldBe("cursor-b");
            second.HasMore.ShouldBeTrue();

            var third = await maintenance.ReindexBlindIndexesAsync<BlindIndexedCustomer>(
                batchSize: 1,
                after: second.NextCursor);
            third.NextCursor!.DeserializeId<string>().ShouldBe("cursor-c");
            third.HasMore.ShouldBeFalse();
        }

        store.Options.Encryption.BlindIndexProvider = activeOnlyProvider;
        await using var read = await store.QuerySessionAsync();
        var afterRetirement = await read.WhereEncryptedEqualsAsync<BlindIndexedCustomer>(
            customer => customer.SocialSecurityNumber,
            "123-45-6789");
        afterRetirement.Select(customer => customer.Id)
            .ShouldBe(["cursor-b", "cursor-c"], ignoreOrder: true);
    }

    [Test]
    public void Blind_index_requires_encryption_and_a_separate_provider()
    {
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        var noEncryption = new StoreOptions();
        noEncryption.Encryption.BlindIndexProvider = blindIndexes;
        noEncryption.Schema.For<BlindIndexedCustomer>()
            .Identity(customer => customer.Id)
            .BlindIndex(customer => customer.SocialSecurityNumber);

        var missingEncryption = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(noEncryption));
        missingEncryption.Message.ShouldContain("must also be mapped");

        var noBlindProvider = new StoreOptions();
        noBlindProvider.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        noBlindProvider.Schema.For<BlindIndexedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber)
            .BlindIndex(customer => customer.SocialSecurityNumber);

        var missingBlindProvider = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(noBlindProvider));
        missingBlindProvider.Message.ShouldContain("BlindIndexProvider");
    }

    [Test]
    public void Blind_index_rejects_nested_selectors_and_storage_field_collisions()
    {
        var options = new StoreOptions();
        Should.Throw<ArgumentException>(() =>
            options.Schema.For<NestedBlindIndexedCustomer>()
                .BlindIndex(customer => customer.Details.SocialSecurityNumber));
        Should.Throw<ArgumentException>(() =>
            options.Schema.For<BlindIndexedCustomer>()
                .BlindIndex(
                    customer => customer.SocialSecurityNumber,
                    index => index.StorageFieldName = "unsafe field"));

        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        var collision = new StoreOptions();
        collision.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        collision.Encryption.BlindIndexProvider = blindIndexes;
        collision.Schema.For<CollidingBlindIndexedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber)
            .BlindIndex(customer => customer.SocialSecurityNumber);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(collision));
        exception.Message.ShouldContain("collides with property");
    }

    [Test]
    public void Provider_rejects_more_than_one_previous_key()
    {
        var exception = Should.Throw<ArgumentException>(() =>
            new HmacSha256BlindIndexProvider(
                "active",
                RandomNumberGenerator.GetBytes(32),
                new Dictionary<string, byte[]>
                {
                    ["previous-1"] = RandomNumberGenerator.GetBytes(32),
                    ["previous-2"] = RandomNumberGenerator.GetBytes(32)
                }));

        exception.Message.ShouldContain("immediately previous");
    }

    [Test]
    public void Equivalent_attribute_and_fluent_mapping_merge_but_conflicts_fail()
    {
        using var wrapping = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32),
            "test-kek-v1",
            "tests");
        using var blindIndexes = new HmacSha256BlindIndexProvider(
            "test-bidx-v1",
            RandomNumberGenerator.GetBytes(32));
        var equivalent = new StoreOptions();
        equivalent.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        equivalent.Encryption.BlindIndexProvider = blindIndexes;
        equivalent.Schema.For<AttributedBlindIndexedCustomer>()
            .BlindIndex(customer => customer.SocialSecurityNumber);

        Should.NotThrow(() => EncryptionMappingValidator.Validate(equivalent));

        var conflicting = new StoreOptions();
        conflicting.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        conflicting.Encryption.BlindIndexProvider = blindIndexes;
        conflicting.Schema.For<AttributedBlindIndexedCustomer>()
            .BlindIndex(
                customer => customer.SocialSecurityNumber,
                index => index.StorageFieldName = "conflicting_bidx");

        var exception = Should.Throw<SableBlindIndexConfigurationException>(
            () => EncryptionMappingValidator.Validate(conflicting));
        exception.Message.ShouldContain("conflict");
    }

    [Test]
    public void Attribute_declared_protection_requires_explicit_schema_registration()
    {
        var options = new StoreOptions();

        var exception = Should.Throw<SableEncryptionConfigurationException>(() =>
            EncryptedFieldResolver.HasEncryptedFields(
                typeof(AttributedBlindIndexedCustomer),
                options.Schema));

        exception.Message.ShouldContain("Schema.For<T>()");
    }

    private static StoreOptions CreateOptions(
        AesGcmKeyWrappingProvider wrapping,
        IBlindIndexTokenProvider blindIndexes)
    {
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Encryption.BlindIndexProvider = blindIndexes;
        options.Schema.For<BlindIndexedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber)
            .BlindIndex(customer => customer.SocialSecurityNumber);
        return options;
    }

    private static bool HasSafeBlindIndexParameter(
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null
            || parameters.Count != 1
            || parameters["blind_index_0"] is not string token)
        {
            return false;
        }

        return token.StartsWith("bidx:v1:", StringComparison.Ordinal)
            && !token.Contains("123456789", StringComparison.Ordinal);
    }

    private sealed class BlindIndexedCustomer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SocialSecurityNumber { get; set; } = "";
    }

    private sealed class BlindIndexStorageProbe
    {
        public string Ciphertext { get; set; } = "";
        public string BlindIndex { get; set; } = "";
    }

    private sealed class NestedBlindIndexedCustomer
    {
        public string Id { get; set; } = "";
        public BlindIndexDetails Details { get; set; } = new();
    }

    private sealed class BlindIndexDetails
    {
        public string SocialSecurityNumber { get; set; } = "";
    }

    private sealed class CollidingBlindIndexedCustomer
    {
        public string Id { get; set; } = "";
        public string SocialSecurityNumber { get; set; } = "";
        public string SocialSecurityNumberBidx { get; set; } = "";
    }

    private sealed class TenantSoftDeletedCustomer : ISoftDeleted
    {
        public string Id { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string SocialSecurityNumber { get; set; } = "";
        public bool Deleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }

    private sealed class TenantBlindIndexedCustomer
    {
        public string Id { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string SocialSecurityNumber { get; set; } = "";
    }

    private sealed class CallbackBlindIndexProvider(
        IBlindIndexTokenProvider inner,
        Func<CancellationToken, Task> callback) : IBlindIndexTokenProvider
    {
        private int _callbackInvoked;

        public ValueTask<string> CreateStorageTokenAsync(
            ReadOnlyMemory<byte> normalizedValue,
            BlindIndexContext context,
            BlindIndexAlgorithm algorithm,
            CancellationToken cancellationToken = default) =>
            inner.CreateStorageTokenAsync(
                normalizedValue, context, algorithm, cancellationToken);

        public async ValueTask<IReadOnlyList<string>> CreateQueryTokensAsync(
            ReadOnlyMemory<byte> normalizedValue,
            BlindIndexContext context,
            BlindIndexAlgorithm algorithm,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.CreateQueryTokensAsync(
                normalizedValue, context, algorithm, cancellationToken);
            if (Interlocked.Exchange(ref _callbackInvoked, 1) == 0)
                await callback(cancellationToken);
            return result;
        }
    }
}

public sealed class AttributedBlindIndexedCustomer : SableDocument<long>
{
    [Encrypt]
    [BlindIndex]
    public string SocialSecurityNumber { get; set; } = "";
}
