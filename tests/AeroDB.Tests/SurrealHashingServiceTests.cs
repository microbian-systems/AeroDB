using AeroDB.Sable;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Tests;

[NotInParallel]
public class SurrealHashingServiceTests
{
    private static readonly SurrealHashAlgorithm[] PasswordAlgorithms =
        [SurrealHashAlgorithm.Argon2, SurrealHashAlgorithm.Bcrypt,
         SurrealHashAlgorithm.Pbkdf2, SurrealHashAlgorithm.Scrypt];

    private static readonly SurrealHashAlgorithm[] DigestAlgorithms =
        [SurrealHashAlgorithm.Blake3, SurrealHashAlgorithm.Joaat,
         SurrealHashAlgorithm.Md5, SurrealHashAlgorithm.Sha1,
         SurrealHashAlgorithm.Sha256, SurrealHashAlgorithm.Sha512];

    [Test]
    public async Task All_password_algorithms_generate_and_verify_on_the_connected_server()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.QuerySessionAsync();
        var hashing = session.Hashing();

        foreach (var algorithm in PasswordAlgorithms)
        {
            var encoded = await hashing.GenerateAsync("correct horse battery staple", algorithm);

            encoded.Algorithm.ShouldBe(algorithm);
            encoded.EncodedHash.ShouldNotBeNullOrWhiteSpace();
            (await hashing.VerifyAsync(
                "correct horse battery staple", encoded)).ShouldBeTrue();
            (await hashing.VerifyAsync(
                "incorrect candidate", encoded)).ShouldBeFalse();
        }
    }

    [Test]
    public async Task Available_digest_algorithms_return_values_and_unavailable_algorithms_fail_stably()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.QuerySessionAsync();
        var hashing = session.Hashing();

        foreach (var algorithm in DigestAlgorithms)
        {
            try
            {
                var digest = await hashing.DigestAsync("AeroDB hash probe", algorithm);
                digest.ShouldNotBeNullOrWhiteSpace();
            }
            catch (SableHashingOperationException exception)
            {
                exception.Message.ShouldNotContain("AeroDB hash probe");
                exception.Message.ShouldContain(algorithm.ToString());
            }
        }
    }

    [Test]
    public async Task Capability_probe_classifies_every_catalog_value_exactly_once()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.QuerySessionAsync();

        var capabilities = await session.Hashing().ProbeCapabilitiesAsync();
        var classified = capabilities.Supported.Concat(capabilities.Unsupported).ToArray();

        classified.Length.ShouldBe(Enum.GetValues<SurrealHashAlgorithm>().Length);
        classified.Distinct().Count().ShouldBe(classified.Length);
        foreach (var algorithm in Enum.GetValues<SurrealHashAlgorithm>())
            classified.ShouldContain(algorithm);
    }

    [Test]
    public async Task Password_compare_uses_closed_function_path_and_bound_hash_then_candidate_parameters()
    {
        var (service, rawSession) = CreateUnitService();
        const string candidate = "candidate-that-must-not-be-logged";
        const string encoded = "$argon2$encoded-value-that-must-not-be-logged";

        var exception = await Should.ThrowAsync<SableHashingOperationException>(
            service.VerifyAsync(
                candidate,
                new SurrealPasswordHash(SurrealHashAlgorithm.Argon2, encoded)));

        exception.Message.ShouldNotContain(candidate);
        exception.Message.ShouldNotContain(encoded);
        await rawSession.Received(1).RawQuery(
            Arg.Is<string>(query =>
                query == "RETURN crypto::argon2::compare($hash, $candidate);"
                && !query.Contains(candidate, StringComparison.Ordinal)
                && !query.Contains(encoded, StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters =>
                parameters != null
                && Equals(parameters["hash"], encoded)
                && Equals(parameters["candidate"], candidate)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Digest_uses_a_closed_function_path_and_bound_value()
    {
        var (service, rawSession) = CreateUnitService();
        const string value = "digest-value-that-must-not-be-logged";

        await Should.ThrowAsync<SableHashingOperationException>(
            service.DigestAsync(value, SurrealHashAlgorithm.Sha256));

        await rawSession.Received(1).RawQuery(
            Arg.Is<string>(query =>
                query == "RETURN crypto::sha256($value);"
                && !query.Contains(value, StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters =>
                parameters != null && Equals(parameters["value"], value)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Password_and_digest_algorithm_families_cannot_be_mixed()
    {
        var (service, rawSession) = CreateUnitService();

        await Should.ThrowAsync<SableHashingConfigurationException>(
            async () => await service.GenerateAsync("value", SurrealHashAlgorithm.Sha256));
        await Should.ThrowAsync<SableHashingConfigurationException>(
            async () => await service.VerifyAsync(
                "value",
                new SurrealPasswordHash(SurrealHashAlgorithm.Blake3, "hash")));
        await Should.ThrowAsync<SableHashingConfigurationException>(
            async () => await service.DigestAsync("value", SurrealHashAlgorithm.Argon2));
        await Should.ThrowAsync<SableHashingConfigurationException>(
            async () => await service.DigestAsync("value", (SurrealHashAlgorithm)999));

        await rawSession.DidNotReceiveWithAnyArgs()
            .RawQuery(default!, default, default);
    }

    [Test]
    public void External_client_logging_assurance_is_required()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var rawSession = Substitute.For<ISurrealDbSession>();
        var options = new StoreOptions { ClientFactory = () => client };
        options.Encryption.ExternalClientUsesProtectedTransport = true;
        var session = new QuerySession(client, rawSession, options, DocumentTracking.None);

        var exception = Should.Throw<SableHashingConfigurationException>(
            () => new SurrealHashingService(session));

        exception.Message.ShouldContain("ExternalClientDisablesProtectedDataLogging");
    }

    [Test]
    public void Arbitrary_query_session_implementations_are_rejected()
    {
        var session = Substitute.For<IQuerySession>();

        Should.Throw<SableHashingConfigurationException>(
            () => new SurrealHashingService(session));
    }

    [Test]
    public void External_client_transport_assurance_is_required()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var rawSession = Substitute.For<ISurrealDbSession>();
        var options = new StoreOptions { ClientFactory = () => client };
        options.Encryption.ExternalClientDisablesProtectedDataLogging = true;
        var session = new QuerySession(client, rawSession, options, DocumentTracking.None);

        var exception = Should.Throw<SableHashingConfigurationException>(
            () => new SurrealHashingService(session));

        exception.Message.ShouldContain("ExternalClientUsesProtectedTransport");
    }

    [Test]
    public void Insecure_non_loopback_endpoint_is_rejected()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var rawSession = Substitute.For<ISurrealDbSession>();
        var options = new StoreOptions { Endpoint = "http://database.example:8000" };
        var session = new QuerySession(client, rawSession, options, DocumentTracking.None);

        var exception = Should.Throw<SableHashingConfigurationException>(
            () => new SurrealHashingService(session));

        exception.Message.ShouldContain("HTTPS/WSS");
    }

    [Test]
    public async Task Capability_probe_does_not_misclassify_operational_failures_as_unsupported()
    {
        var (service, _) = CreateUnitService();

        await Should.ThrowAsync<SableHashingOperationException>(
            async () => await service.ProbeCapabilitiesAsync());
    }

    private static async Task<IDocumentStore> CreateStoreAsync() =>
        await TestHarness.CreateStoreAsync(options =>
        {
            options.Encryption.ExternalClientDisablesProtectedDataLogging = true;
            options.Encryption.ExternalClientUsesProtectedTransport = true;
        });

    private static (SurrealHashingService Service, ISurrealDbSession Session) CreateUnitService()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var rawSession = Substitute.For<ISurrealDbSession>();
        rawSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        var options = new StoreOptions
        {
            ClientFactory = () => client
        };
        options.Encryption.ExternalClientDisablesProtectedDataLogging = true;
        options.Encryption.ExternalClientUsesProtectedTransport = true;
        var query = new QuerySession(client, rawSession, options, DocumentTracking.None);
        return (new SurrealHashingService(query), rawSession);
    }
}
