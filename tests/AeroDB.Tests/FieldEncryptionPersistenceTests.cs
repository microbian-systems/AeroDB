using System.Security.Cryptography;
using AeroDB.Sable;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using Microsoft.Extensions.Logging;

namespace AeroDB.Tests;

public class FieldEncryptionPersistenceTests
{
    [Test]
    public async Task Fluent_encrypted_field_round_trips_through_store_load_and_clear_predicate()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<EncryptedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber)
                .EncryptField(customer => customer.PrivateBytes);
        });

        await using (var write = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            write.Store(new EncryptedCustomer
            {
                Id = "customer-42",
                Name = "Ada",
                SocialSecurityNumber = "123-45-6789",
                PrivateBytes = [1, 2, 3, 4]
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        var loaded = await read.LoadAsync<EncryptedCustomer>("customer-42");
        loaded.ShouldNotBeNull();
        loaded!.SocialSecurityNumber.ShouldBe("123-45-6789");
        loaded.PrivateBytes.ShouldBe([1, 2, 3, 4]);

        var queried = await read.Query<EncryptedCustomer>()
            .Where(customer => customer.Name == "Ada")
            .ToListAsync();
        queried.ShouldHaveSingleItem().SocialSecurityNumber.ShouldBe("123-45-6789");
    }

    [Test]
    public async Task Attribute_encrypted_string_and_bytes_round_trip()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<AttributedEncryptedCustomer>()
                .Identity(customer => customer.Id);
        });

        await using (var write = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            write.Store(new AttributedEncryptedCustomer
            {
                Id = 731,
                Secret = "attribute-secret",
                SecretBytes = [0, 1, 2, 254, 255]
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        var loaded = await read.LoadAsync<AttributedEncryptedCustomer>("731");
        loaded.ShouldNotBeNull();
        loaded!.Secret.ShouldBe("attribute-secret");
        loaded.SecretBytes.ShouldBe([0, 1, 2, 254, 255]);
    }

    [Test]
    public async Task Fluent_encrypted_numeric_id_POCO_uses_the_decrypting_load_path()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<EncryptedLongCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.Secret);
        });

        await using (var write = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            write.Store(new EncryptedLongCustomer
            {
                Id = 90210,
                Name = "numeric",
                Secret = "numeric-id-secret"
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        var loaded = await read.LoadAsync<EncryptedLongCustomer>(90210L);

        loaded.ShouldNotBeNull();
        loaded!.Secret.ShouldBe("numeric-id-secret");
    }

    [Test]
    public async Task Encrypted_document_optimistic_concurrency_reads_only_clear_version_metadata()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.UseOptimisticConcurrency = true;
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<EncryptedVersionedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.Secret);
        });

        await using (var seed = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            var customer = new EncryptedVersionedCustomer
            {
                Id = 5544,
                Name = "versioned",
                Secret = "versioned-secret"
            };
            seed.Store(customer);
            await seed.SaveChangesAsync();
            customer.Version.ShouldBe(1);
        }

        await using var update = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        var loaded = await update.LoadAsync<EncryptedVersionedCustomer>(5544L);
        loaded.ShouldNotBeNull();
        loaded!.Secret.ShouldBe("versioned-secret");
        loaded.Version.ShouldBe(1);
        loaded.Name = "updated";
        update.AddOperation(loaded, OperationType.Modified);

        await update.SaveChangesAsync();

        loaded.Version.ShouldBe(2);
    }

    [Test]
    public async Task Save_writes_envelope_and_never_embeds_encrypted_plaintext()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<EncryptedWriteProbe>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber);
        });
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new EncryptedWriteProbe
        {
            Id = "no-plaintext",
            Name = "Grace",
            SocialSecurityNumber = "987-65-4321"
        });

        await session.SaveChangesAsync();

        var stored = await session.RawQueryAsync<EnvelopeStorageProbe>(
            "SELECT social_security_number.ciphertext AS ciphertext, " +
            "social_security_number.wrapped_key_ciphertext AS wrapped_key_ciphertext " +
            "FROM encrypted_write_probe:`no-plaintext`;");

        stored.Count.ShouldBe(1);
        stored[0].Ciphertext.ShouldNotBeNullOrWhiteSpace();
        stored[0].WrappedKeyCiphertext.ShouldNotBeNullOrWhiteSpace();
        stored[0].Ciphertext.ShouldNotContain("987-65-4321");
    }

    [Test]
    public async Task Encrypted_write_logs_redact_plaintext_and_envelope_material()
    {
        var logger = new CapturingLogger();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.LoggerFactory = loggerFactory;
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<EncryptedWriteProbe>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.SocialSecurityNumber);
        });
        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new EncryptedWriteProbe
        {
            Id = "logged",
            Name = "Katherine",
            SocialSecurityNumber = "111-22-3333"
        });

        await session.SaveChangesAsync();

        logger.Messages.ShouldContain(message =>
            message.Contains("<encrypted write redacted>", StringComparison.Ordinal));
        logger.Messages.ShouldAllBe(message =>
            !message.Contains("111-22-3333", StringComparison.Ordinal)
            && !message.Contains("wrapped_key_ciphertext", StringComparison.Ordinal)
            && !message.Contains("ciphertext:", StringComparison.Ordinal));
    }

    [Test]
    public async Task Encrypted_document_without_id_fails_before_database_write()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        using var wrapping = CreateWrappingProvider();
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Schema.For<EncryptedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber);
        var session = new DocumentSession(client, surrealSession, options, DocumentTracking.None);
        session.Store(new EncryptedCustomer
        {
            Id = "",
            SocialSecurityNumber = "123-45-6789"
        });

        await Should.ThrowAsync<SableEncryptedDocumentRequiresIdException>(
            () => session.SaveChangesAsync());
        await surrealSession.DidNotReceiveWithAnyArgs()
            .RawQuery(default!, default, default);
    }

    [Test]
    public async Task Runtime_guard_rejects_fluent_encrypted_field_predicate()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        using var wrapping = CreateWrappingProvider();
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Schema.For<EncryptedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber);
        var session = new DocumentSession(client, surrealSession, options, DocumentTracking.None);

        var query = session.Query<EncryptedCustomer>()
            .Where(customer => customer.SocialSecurityNumber == "123-45-6789");
        await Should.ThrowAsync<SableEncryptedOperationNotSupportedException>(
            () => query.ToListAsync());
        await surrealSession.DidNotReceiveWithAnyArgs()
            .RawQuery(default!, default, default);
    }

    [Test]
    public void Fluent_mapping_rejects_unsupported_type_and_undefined_algorithm()
    {
        var options = new StoreOptions();
        Should.Throw<ArgumentException>(() =>
            options.Schema.For<EncryptedCustomer>().EncryptField(customer => customer.Age));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            options.Schema.For<EncryptedCustomer>().EncryptField(
                customer => customer.SocialSecurityNumber,
                (EncryptionAlgorithm)999));
    }

    [Test]
    public async Task ChaCha20_encrypted_field_round_trips_through_persistence()
    {
        if (!ChaCha20Poly1305.IsSupported)
            return;

        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AeadEnvelopeDataProtectionProvider(wrapping);
            options.Schema.For<ChaChaEncryptedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(
                    customer => customer.Secret,
                    EncryptionAlgorithm.ChaCha20Poly1305);
        });

        await using (var write = await store.LightweightSessionAsync())
        {
            write.Store(new ChaChaEncryptedCustomer
            {
                Id = "chacha-customer",
                Secret = "ChaCha secret"
            });
            await write.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        var loaded = await read.LoadAsync<ChaChaEncryptedCustomer>("chacha-customer");

        loaded.ShouldNotBeNull();
        loaded!.Secret.ShouldBe("ChaCha secret");
    }

    [Test]
    public async Task Corrupted_database_envelope_fails_closed_on_every_supported_typed_read()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<CorruptionEncryptedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.Secret);
        });

        await using (var write = (DocumentSession)await store.LightweightSessionAsync())
        {
            write.Store(new CorruptionEncryptedCustomer
            {
                Id = "corrupted-customer",
                Name = "corrupted",
                Secret = "123-45-6789"
            });
            await write.SaveChangesAsync();
            var tamper = await write.Session.RawQuery(
                "UPDATE corruption_encrypted_customer:`corrupted-customer` " +
                "SET secret.ciphertext = 'AAAAAAAAAAAAAAAA';");
            tamper.EnsureAllOks();
        }

        await using (var load = await store.QuerySessionAsync())
        {
            await Should.ThrowAsync<SableEncryptionAuthenticationException>(
                async () => await load.LoadAsync<CorruptionEncryptedCustomer>("corrupted-customer"));
        }

        await using (var linq = await store.QuerySessionAsync())
        {
            await Should.ThrowAsync<SableEncryptionAuthenticationException>(
                async () => await linq.Query<CorruptionEncryptedCustomer>()
                    .Where(customer => customer.Name == "corrupted")
                    .ToListAsync());
        }

        await using (var raw = await store.QuerySessionAsync())
        {
            await Should.ThrowAsync<SableEncryptionAuthenticationException>(
                async () => await raw.RawQueryAsync<CorruptionEncryptedCustomer>(
                    "SELECT * FROM corruption_encrypted_customer WHERE name = $name;",
                    new Dictionary<string, object?> { ["name"] = "corrupted" }));
        }
    }

    [Test]
    public async Task Missing_encrypted_storage_field_cannot_materialize_a_default_value()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<CorruptionEncryptedCustomer>()
                .Identity(customer => customer.Id)
                .EncryptField(customer => customer.Secret);
        });

        await using (var write = (DocumentSession)await store.LightweightSessionAsync())
        {
            write.Store(new CorruptionEncryptedCustomer
            {
                Id = "missing-envelope",
                Secret = "must not become a default"
            });
            await write.SaveChangesAsync();
            (await write.Session.RawQuery(
                "UPDATE corruption_encrypted_customer:`missing-envelope` UNSET secret;"))
                .EnsureAllOks();
        }

        await using var read = await store.QuerySessionAsync();
        var exception = await Should.ThrowAsync<SableEnvelopeException>(
            async () => await read.LoadAsync<CorruptionEncryptedCustomer>("missing-envelope"));
        exception.Message.ShouldContain("is missing");
    }

    [Test]
    public async Task Insert_and_update_apply_tenant_scope_and_custom_table_AAD()
    {
        using var wrapping = CreateWrappingProvider();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
            options.Schema.For<TenantEncryptedCustomer>()
                .TableName("secure_tenant_customers")
                .Identity(customer => customer.Id)
                .MultiTenanted()
                .EncryptField(customer => customer.Secret);
        });

        var inserted = new TenantEncryptedCustomer
        {
            Id = "tenant-customer",
            Secret = "inserted secret"
        };
        await using (var insert = await store.LightweightSessionAsync())
        {
            insert.SetTenant("tenant-a");
            insert.Insert(inserted);
            await insert.SaveChangesAsync();
        }
        inserted.TenantId.ShouldBe("tenant-a");

        var updated = new TenantEncryptedCustomer
        {
            Id = "tenant-customer",
            Secret = "updated secret"
        };
        await using (var update = await store.LightweightSessionAsync())
        {
            update.SetTenant("tenant-a");
            update.Update(updated);
            await update.SaveChangesAsync();
        }
        updated.TenantId.ShouldBe("tenant-a");

        await using var read = await store.QuerySessionAsync();
        read.SetTenant("tenant-a");
        var loaded = await read.LoadAsync<TenantEncryptedCustomer>("tenant-customer");
        loaded.ShouldNotBeNull();
        loaded!.Secret.ShouldBe("updated secret");
        loaded.TenantId.ShouldBe("tenant-a");
    }

    [Test]
    public async Task LoadMany_does_not_translate_encryption_failure_to_an_empty_result()
    {
        var session = Substitute.For<IQuerySession>();
        session.RawQueryAsync<EncryptedCustomer>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<EncryptedCustomer>>(
                new SableEnvelopeException("tampered envelope")));

        var exception = await Should.ThrowAsync<SableEnvelopeException>(
            () => LoadManyExtensions.LoadManyAsync<EncryptedCustomer>(
                session,
                ["customer-42"]));

        exception.Message.ShouldContain("tampered envelope");
    }

    [Test]
    public async Task Unsupported_read_surfaces_fail_before_database_access()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        using var wrapping = CreateWrappingProvider();
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Schema.For<EncryptedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber);
        var session = new DocumentSession(
            client,
            surrealSession,
            options,
            DocumentTracking.None);

        Should.Throw<SableEncryptedOperationNotSupportedException>(
            () => session.DeserializeMappedPocoResponse<EncryptedCustomer>(
                new SurrealDbResponse([])));
        Should.Throw<SableEncryptedOperationNotSupportedException>(
            () => session.Graph<EncryptedCustomer>());
        Should.Throw<SableEncryptedOperationNotSupportedException>(
            () => SearchQueryExtensions.Search<EncryptedCustomer>(session));
        Should.Throw<SableEncryptedOperationNotSupportedException>(
            () => SpatialExtensions.Spatial<EncryptedCustomer>(session));
        Should.Throw<SableEncryptedOperationNotSupportedException>(
            () => TimeSeriesExtensions.TimeSeries<EncryptedCustomer>(session));
        Should.Throw<SableEncryptedOperationNotSupportedException>(
            () => session.Graph<ClearRelatedCustomer>()
                .Out<EncryptedCustomer>("related"));
        await Should.ThrowAsync<SableEncryptedOperationNotSupportedException>(
            async () => await session.Query<ClearRelatedCustomer>()
                .IncludeBatch(
                    customer => customer.EncryptedCustomerId,
                    (EncryptedCustomer _) => { })
                .ToListAsync());
        await Should.ThrowAsync<SableEncryptedOperationNotSupportedException>(
            () => session.WatchTableAsync<EncryptedCustomer>());
        await Should.ThrowAsync<SableEncryptedOperationNotSupportedException>(
            () => session.DeleteWhere<EncryptedCustomer>(
                customer => customer.SocialSecurityNumber == "123-45-6789"));

        await surrealSession.DidNotReceiveWithAnyArgs()
            .RawQuery(default!, default, default);
    }

    [Test]
    public async Task Public_bulk_helper_rejects_encrypted_models_before_database_access()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        using var wrapping = CreateWrappingProvider();
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Schema.For<EncryptedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.SocialSecurityNumber);
        var session = new DocumentSession(
            client,
            surrealSession,
            options,
            DocumentTracking.None);

        await Should.ThrowAsync<SableEncryptedOperationNotSupportedException>(
            async () => await BulkOperations.BulkInsertAsync(
                session,
                new[]
                {
                    new EncryptedCustomer
                    {
                        Id = "bulk-secret",
                        SocialSecurityNumber = "123-45-6789"
                    }
                }));

        await surrealSession.DidNotReceiveWithAnyArgs()
            .RawQuery(default!, default, default);
    }

    [Test]
    public async Task Public_bulk_helper_rejects_encrypted_derived_runtime_types()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        using var wrapping = CreateWrappingProvider();
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        options.Schema.For<BulkEncryptedDerivedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.Secret);
        var session = new DocumentSession(
            client,
            surrealSession,
            options,
            DocumentTracking.None);
        IReadOnlyList<BulkBaseCustomer> documents =
        [
            new BulkEncryptedDerivedCustomer
            {
                Id = "polymorphic-secret",
                Secret = "must-not-be-plaintext"
            }
        ];

        await Should.ThrowAsync<SableEncryptedOperationNotSupportedException>(
            async () => await BulkOperations.BulkInsertAsync(session, documents));

        await surrealSession.DidNotReceiveWithAnyArgs()
            .RawQuery(default!, default, default);
    }

    private static AesGcmKeyWrappingProvider CreateWrappingProvider()
        => new(RandomNumberGenerator.GetBytes(32), "test-kek-v1", "tests");

    private sealed class EncryptedCustomer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SocialSecurityNumber { get; set; } = "";
        public byte[] PrivateBytes { get; set; } = [];
        public int Age { get; set; }
    }

    private sealed class EnvelopeStorageProbe
    {
        public string Ciphertext { get; set; } = "";
        public string WrappedKeyCiphertext { get; set; } = "";
    }

    private sealed class EncryptedWriteProbe
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SocialSecurityNumber { get; set; } = "";
    }

    private sealed class EncryptedLongCustomer
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private sealed class ChaChaEncryptedCustomer
    {
        public string Id { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private class BulkBaseCustomer
    {
        public string Id { get; set; } = "";
    }

    private sealed class BulkEncryptedDerivedCustomer : BulkBaseCustomer
    {
        public string Secret { get; set; } = "";
    }

    private sealed class TenantEncryptedCustomer
    {
        public string Id { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private sealed class CorruptionEncryptedCustomer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private sealed class ClearRelatedCustomer
    {
        public string Id { get; set; } = "";
        public string EncryptedCustomerId { get; set; } = "";
    }

    private sealed class EncryptedVersionedCustomer : IVersioned
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Secret { get; set; } = "";
        public long Version { get; set; }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

public sealed class AttributedEncryptedCustomer : SableDocument<long>
{
    [Encrypt]
    public string Secret { get; set; } = "";

    [Encrypt(EncryptionAlgorithm.Aes256Gcm)]
    public byte[] SecretBytes { get; set; } = [];
}
