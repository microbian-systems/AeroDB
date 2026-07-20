using System.Security.Cryptography;
using System.Text;
using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class AesGcmDataProtectionProviderTests
{
    [Test]
    public async Task Protect_and_unprotect_string_round_trips()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var expected = "Sensitive data: 123-45-6789 🌊";

        var envelope = await provider.ProtectAsync(Encoding.UTF8.GetBytes(expected), context);
        using var plaintext = await provider.UnprotectAsync(envelope, context);

        Encoding.UTF8.GetString(plaintext.Memory.Span).ShouldBe(expected);
        envelope.Version.ShouldBe(EncryptedEnvelope.CurrentFormatVersion);
        envelope.Algorithm.ShouldBe(EncryptionAlgorithm.Aes256Gcm);
        envelope.WrappingAlgorithm.ShouldBe(KeyWrappingAlgorithm.SableAes256Gcm);
        envelope.ProviderId.ShouldBe("test-provider");
        envelope.KeyId.ShouldBe("test-key-v1");
        envelope.CodecId.ShouldBe(context.CodecId);
    }

    [Test]
    public async Task Protect_and_unprotect_binary_data_round_trips()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext(codecId: "bytes-v1");
        byte[] expected = [0, 1, 2, 127, 128, 254, 255, 0, 42];

        var envelope = await provider.ProtectAsync(expected, context);
        using var plaintext = await provider.UnprotectAsync(envelope, context);

        plaintext.Memory.ToArray().ShouldBe(expected);
    }

    [Test]
    public async Task Protecting_the_same_plaintext_twice_produces_randomized_envelopes()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var plaintext = Encoding.UTF8.GetBytes("same plaintext");

        var first = await provider.ProtectAsync(plaintext, context);
        var second = await provider.ProtectAsync(plaintext, context);

        first.Nonce.SequenceEqual(second.Nonce).ShouldBeFalse();
        first.Ciphertext.SequenceEqual(second.Ciphertext).ShouldBeFalse();
        first.WrappedKeyNonce.SequenceEqual(second.WrappedKeyNonce).ShouldBeFalse();
        first.WrappedKeyCiphertext.SequenceEqual(second.WrappedKeyCiphertext).ShouldBeFalse();
    }

    [Test]
    public async Task Unprotect_rejects_tampered_payload_ciphertext()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        var tampered = envelope with { Ciphertext = Tamper(envelope.Ciphertext) };

        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(tampered, context));
    }

    [Test]
    public async Task Unprotect_rejects_tampered_wrapped_key_ciphertext()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        var tampered = envelope with
        {
            WrappedKeyCiphertext = Tamper(envelope.WrappedKeyCiphertext)
        };

        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(tampered, context));
    }

    [Test]
    public async Task Unprotect_rejects_tampering_with_every_remaining_envelope_component()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        EncryptedEnvelope[] tamperedEnvelopes =
        [
            envelope with { Version = envelope.Version + 1 },
            envelope with { Algorithm = EncryptionAlgorithm.ChaCha20Poly1305 },
            envelope with { ProviderId = envelope.ProviderId + "-tampered" },
            envelope with { KeyId = envelope.KeyId + "-tampered" },
            envelope with { WrappingAlgorithm = (KeyWrappingAlgorithm)999 },
            envelope with { CodecId = envelope.CodecId + "-tampered" },
            envelope with { Nonce = Tamper(envelope.Nonce) },
            envelope with { Tag = Tamper(envelope.Tag) },
            envelope with { WrappedKeyNonce = Tamper(envelope.WrappedKeyNonce) },
            envelope with { WrappedKeyTag = Tamper(envelope.WrappedKeyTag) }
        ];

        foreach (var tampered in tamperedEnvelopes)
        {
            await Should.ThrowAsync<SableEncryptionException>(
                async () => await provider.UnprotectAsync(tampered, context));
        }
    }

    [Test]
    public async Task Unprotect_rejects_a_different_record_in_authenticated_context()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var originalContext = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), originalContext);
        var wrongContext = originalContext with { RecordId = "customer:1000" };

        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(envelope, wrongContext));
    }

    [Test]
    public async Task Unprotect_rejects_every_other_changed_AAD_scope_component()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var originalContext = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), originalContext);
        EncryptionContext[] wrongContexts =
        [
            originalContext with { Namespace = "other-namespace" },
            originalContext with { Database = "other-database" },
            originalContext with { Table = "other-table" }
        ];

        foreach (var wrongContext in wrongContexts)
        {
            await Should.ThrowAsync<SableEncryptionAuthenticationException>(
                async () => await provider.UnprotectAsync(envelope, wrongContext));
        }
    }

    [Test]
    public async Task Unprotect_rejects_a_different_field_in_authenticated_context()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var originalContext = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), originalContext);
        var wrongContext = originalContext with { StorageField = "date_of_birth" };

        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(envelope, wrongContext));
    }

    [Test]
    public async Task Unprotect_rejects_a_different_tenant_in_authenticated_context()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var originalContext = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), originalContext);
        var wrongContext = originalContext with { TenantId = "tenant-b" };

        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(envelope, wrongContext));
    }

    [Test]
    public async Task ChaCha20_Poly1305_round_trips_or_fails_with_platform_capability_error()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AeadEnvelopeDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();

        if (!ChaCha20Poly1305.IsSupported)
        {
            var exception = await Should.ThrowAsync<PlatformNotSupportedException>(
                async () => await provider.ProtectAsync(
                    "secret"u8.ToArray(),
                    context,
                    EncryptionAlgorithm.ChaCha20Poly1305));
            exception.Message.ShouldContain("not supported on this platform");
            return;
        }

        var envelope = await provider.ProtectAsync(
                "secret"u8.ToArray(),
                context,
                EncryptionAlgorithm.ChaCha20Poly1305);
        using var plaintext = await provider.UnprotectAsync(envelope, context);

        envelope.Algorithm.ShouldBe(EncryptionAlgorithm.ChaCha20Poly1305);
        Encoding.UTF8.GetString(plaintext.Memory.Span).ShouldBe("secret");
    }

    [Test]
    public async Task ChaCha20_Poly1305_rejects_tampering_and_wrong_AAD()
    {
        if (!ChaCha20Poly1305.IsSupported)
            return;

        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AeadEnvelopeDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync(
            "secret"u8.ToArray(),
            context,
            EncryptionAlgorithm.ChaCha20Poly1305);

        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(
                envelope with { Ciphertext = Tamper(envelope.Ciphertext) },
                context));
        await Should.ThrowAsync<SableEncryptionAuthenticationException>(
            async () => await provider.UnprotectAsync(
                envelope,
                context with { RecordId = "customer:other" }));
    }

    [Test]
    public async Task Unprotect_rejects_invalid_payload_nonce_size_before_decryption()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        var malformed = envelope with { Nonce = new byte[11] };

        var exception = await Should.ThrowAsync<SableEnvelopeException>(
            async () => await provider.UnprotectAsync(malformed, context));

        exception.Message.ShouldContain("invalid AES-256-GCM nonce or tag size");
    }

    [Test]
    public async Task Unprotect_rejects_invalid_payload_tag_size_before_decryption()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        var malformed = envelope with { Tag = new byte[15] };

        var exception = await Should.ThrowAsync<SableEnvelopeException>(
            async () => await provider.UnprotectAsync(malformed, context));

        exception.Message.ShouldContain("invalid AES-256-GCM nonce or tag size");
    }

    [Test]
    public async Task Unprotect_rejects_invalid_wrapped_key_size_before_decryption()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        var malformed = envelope with { WrappedKeyCiphertext = new byte[31] };

        var exception = await Should.ThrowAsync<SableEnvelopeException>(
            async () => await provider.UnprotectAsync(malformed, context));

        exception.Message.ShouldContain("Wrapped DEK has invalid AES-256-GCM sizes");
    }

    [Test]
    public async Task Unprotect_honors_the_configured_plaintext_limit()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var writer = new AesGcmDataProtectionProvider(
            keyWrappingProvider,
            maximumPlaintextBytes: 64);
        var reader = new AesGcmDataProtectionProvider(
            keyWrappingProvider,
            maximumPlaintextBytes: 8);
        var context = CreateContext();
        var envelope = await writer.ProtectAsync(
            "plaintext longer than eight bytes"u8.ToArray(),
            context);

        var exception = await Should.ThrowAsync<SableEnvelopeException>(
            async () => await reader.UnprotectAsync(envelope, context));

        exception.Message.ShouldContain("configured maximum 8");
    }

    [Test]
    public void Envelope_parse_rejects_oversized_base64_before_decoding()
    {
        var storage = new EncryptedEnvelope
        {
            Version = EncryptedEnvelope.CurrentFormatVersion,
            Algorithm = EncryptionAlgorithm.Aes256Gcm,
            ProviderId = "test-provider",
            KeyId = "test-key-v1",
            WrappingAlgorithm = KeyWrappingAlgorithm.SableAes256Gcm,
            CodecId = "utf8-string-v1",
            Nonce = new byte[12],
            Ciphertext = new byte[1],
            Tag = new byte[16],
            WrappedKeyNonce = new byte[12],
            WrappedKeyCiphertext = new byte[32],
            WrappedKeyTag = new byte[16]
        }.ToStorageObject();
        storage["ciphertext"] = new string('A', 1024);

        var exception = Should.Throw<SableEnvelopeException>(
            () => EncryptedEnvelope.Parse(storage, maximumCiphertextBytes: 8));

        exception.Message.ShouldContain("maximum encoded size");
    }

    [Test]
    public void Canonical_payload_AAD_matches_the_v1_golden_vector()
    {
        var aad = CanonicalEncryptionAad.EncodePayload(
            CreateContext(),
            EncryptionAlgorithm.Aes256Gcm);

        Convert.ToHexString(aad).ShouldBe(
            "5341424C452D4649454C442D414144000000010000000100000000000000046165726F" +
            "00000009637573746F6D65727300000008637573746F6D65720000000C637573746F6D" +
            "65723A3939390000000874656E616E742D6100000016736F6369616C5F736563757269" +
            "74795F6E756D6265720000000E757466382D737472696E672D76310000000000000000");
    }

    [Test]
    public async Task Disposing_owned_plaintext_zeroes_the_owned_buffer_and_blocks_access()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var provider = new AesGcmDataProtectionProvider(keyWrappingProvider);
        var context = CreateContext();
        var envelope = await provider.ProtectAsync("secret"u8.ToArray(), context);
        var plaintext = await provider.UnprotectAsync(envelope, context);
        var retainedView = plaintext.Memory;

        plaintext.Dispose();

        retainedView.ToArray().ShouldAllBe(value => value == 0);
        Should.Throw<ObjectDisposedException>(() => _ = plaintext.Memory);
    }

    [Test]
    public async Task Disposing_owned_key_material_zeroes_the_owned_buffer_and_blocks_access()
    {
        using var keyWrappingProvider = CreateKeyWrappingProvider();
        var context = new KeyWrappingContext(CreateContext(), EncryptionAlgorithm.Aes256Gcm);
        var wrappedKey = await keyWrappingProvider.WrapAsync(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            context);
        var keyMaterial = await keyWrappingProvider.UnwrapAsync(wrappedKey, context);
        var retainedView = keyMaterial.Memory;

        keyMaterial.Dispose();

        retainedView.ToArray().ShouldAllBe(value => value == 0);
        Should.Throw<ObjectDisposedException>(() => _ = keyMaterial.Memory);
    }

    [Test]
    public void Disposing_key_wrapping_provider_blocks_future_use()
    {
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var keyWrappingProvider = new AesGcmKeyWrappingProvider(
            key,
            "test-key-v1",
            "test-provider");

        keyWrappingProvider.Dispose();

        Should.Throw<ObjectDisposedException>(() =>
            keyWrappingProvider.WrapAsync(
                RandomNumberGenerator.GetBytes(32),
                new KeyWrappingContext(CreateContext(), EncryptionAlgorithm.Aes256Gcm)));
    }

    [Test]
    public async Task Key_ring_reads_previous_keys_but_writes_only_the_active_key()
    {
        using var previous = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "key-v1", "test-provider");
        using var active = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "key-v2", "test-provider");
        var context = CreateContext();
        var oldProvider = new AeadEnvelopeDataProtectionProvider(previous);
        var oldEnvelope = await oldProvider.ProtectAsync("secret"u8.ToArray(), context);
        var rotatingProvider = new AeadEnvelopeDataProtectionProvider(
            new KeyRingWrappingProvider(active, previous));

        using var recovered = await rotatingProvider.UnprotectAsync(oldEnvelope, context);
        var newEnvelope = await rotatingProvider.ProtectAsync("new secret"u8.ToArray(), context);

        Encoding.UTF8.GetString(recovered.Memory.Span).ShouldBe("secret");
        oldEnvelope.KeyId.ShouldBe("key-v1");
        newEnvelope.KeyId.ShouldBe("key-v2");
    }

    [Test]
    public async Task Rewrap_changes_only_wrapped_DEK_material_and_survives_previous_key_retirement()
    {
        using var previous = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "key-v1", "test-provider");
        using var active = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "key-v2", "test-provider");
        var context = CreateContext();
        var originalProvider = new AeadEnvelopeDataProtectionProvider(previous);
        var original = await originalProvider.ProtectAsync("secret"u8.ToArray(), context);
        var rotatingProvider = new AeadEnvelopeDataProtectionProvider(
            new KeyRingWrappingProvider(active, previous));

        var rewrapped = await rotatingProvider.RewrapAsync(original, context);

        rewrapped.KeyId.ShouldBe("key-v2");
        rewrapped.Nonce.ShouldBe(original.Nonce);
        rewrapped.Ciphertext.ShouldBe(original.Ciphertext);
        rewrapped.Tag.ShouldBe(original.Tag);
        rewrapped.WrappedKeyCiphertext.ShouldNotBe(original.WrappedKeyCiphertext);
        var activeOnly = new AeadEnvelopeDataProtectionProvider(active);
        using var recovered = await activeOnly.UnprotectAsync(rewrapped, context);
        Encoding.UTF8.GetString(recovered.Memory.Span).ShouldBe("secret");
        await Should.ThrowAsync<SableEnvelopeException>(
            async () => await activeOnly.UnprotectAsync(original, context));
    }

    [Test]
    public void Key_ring_rejects_duplicate_routes()
    {
        using var first = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "key-v1", "test-provider");
        using var duplicate = new AesGcmKeyWrappingProvider(
            RandomNumberGenerator.GetBytes(32), "key-v1", "test-provider");

        Should.Throw<ArgumentException>(
            () => new KeyRingWrappingProvider(first, duplicate));
    }

    private static AesGcmKeyWrappingProvider CreateKeyWrappingProvider()
        => new(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            "test-key-v1",
            "test-provider");

    private static EncryptionContext CreateContext(string codecId = "utf8-string-v1")
        => new(
            Namespace: "aero",
            Database: "customers",
            Table: "customer",
            RecordId: "customer:999",
            TenantId: "tenant-a",
            StorageField: "social_security_number",
            CodecId: codecId);

    private static byte[] Tamper(byte[] value)
    {
        var tampered = value.ToArray();
        tampered[0] ^= 0x01;
        return tampered;
    }
}
