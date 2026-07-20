using System.Security.Cryptography;
using AeroDB.Sable;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;

namespace AeroDB.Tests;

public sealed class EncryptionMappingValidatorTests
{
    [Test]
    public void Valid_encrypted_mapping_passes_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.Secret);

        Should.NotThrow(() => EncryptionMappingValidator.Validate(options));
    }

    [Test]
    public void Missing_provider_fails_startup_validation()
    {
        var options = new StoreOptions();
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.Secret);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("Encryption.Provider");
    }

    [Test]
    public void Missing_stable_identity_fails_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<ValidatedCustomer>()
            .EncryptField(customer => customer.Secret);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("stable application-assigned identity");
    }

    [Test]
    public void EncryptField_rejects_the_configured_identity_immediately()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        var mapping = options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => mapping.EncryptField(customer => customer.Id));

        exception.Message.ShouldContain("identities must remain clear and stable");
        mapping.GetEncryptedFields().ShouldBeEmpty();
    }

    [Test]
    public void EncryptField_rejects_the_conventional_Id_immediately()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        var mapping = options.Schema.For<ValidatedCustomer>();

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => mapping.EncryptField(customer => customer.Id));

        exception.Message.ShouldContain("identities must remain clear and stable");
        mapping.GetEncryptedFields().ShouldBeEmpty();
    }

    [Test]
    public void Identity_rejects_a_previously_encrypted_custom_key_immediately()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        var mapping = options.Schema.For<ValidatedCustomer>()
            .EncryptField(customer => customer.CustomKey);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => mapping.Identity(customer => customer.CustomKey));

        exception.Message.ShouldContain("identities must remain clear and stable");
        mapping.IdentityProperty.ShouldBeNull();
    }

    [Test]
    public void Attribute_encrypted_identity_fails_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<AttributedEncryptedIdentityCustomer>();

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("document identity");
    }

    [Test]
    public void Encrypted_indexed_field_fails_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.Secret)
            .Index(customer => customer.Secret);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("database index");
    }

    [Test]
    public void Encrypted_tenant_routing_field_fails_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .MultiTenanted()
            .EncryptField(customer => customer.TenantId);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("tenant routing");
    }

    [Test]
    public void Encrypted_relationship_field_fails_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<RelatedPerson>()
            .Identity(person => person.Id);
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .HasOne<RelatedPerson>(customer => customer.RelatedPersonId);
        options.Schema.For<ValidatedCustomer>()
            .EncryptField(customer => customer.RelatedPersonId);
        options.Schema.ResolveRelationships();

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("relationship link");
    }

    [Test]
    public void Encrypted_change_tracked_document_fails_startup_validation()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.Secret)
            .ChangeTracking();

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));

        exception.Message.ShouldContain("change tracking");
    }

    [Test]
    public void External_client_requires_explicit_protected_logging_assurance()
    {
        using var wrapping = CreateWrappingProvider();
        var options = CreateOptions(wrapping);
        options.ClientFactory = () => Substitute.For<ISurrealDbClient>();
        options.Schema.For<ValidatedCustomer>()
            .Identity(customer => customer.Id)
            .EncryptField(customer => customer.Secret);

        var exception = Should.Throw<SableEncryptionConfigurationException>(
            () => EncryptionMappingValidator.Validate(options));
        exception.Message.ShouldContain("ExternalClientDisablesProtectedDataLogging");

        options.Encryption.ExternalClientDisablesProtectedDataLogging = true;
        Should.NotThrow(() => EncryptionMappingValidator.Validate(options));
    }

    private static StoreOptions CreateOptions(AesGcmKeyWrappingProvider wrapping)
    {
        var options = new StoreOptions();
        options.Encryption.Provider = new AesGcmDataProtectionProvider(wrapping);
        return options;
    }

    private static AesGcmKeyWrappingProvider CreateWrappingProvider()
        => new(RandomNumberGenerator.GetBytes(32), "test-kek-v1", "tests");

    private sealed class ValidatedCustomer
    {
        public string Id { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string Secret { get; set; } = "";
        public string CustomKey { get; set; } = "";
        public string RelatedPersonId { get; set; } = "";
    }

    private sealed class RelatedPerson
    {
        public string Id { get; set; } = "";
    }
}

public sealed class AttributedEncryptedIdentityCustomer : ISableDocument<string>
{
    [Encrypt]
    public string Id { get; set; } = "";
}
