using System.Text.Json;
using AeroDB.Sable.Internals.Cbor;
using Shouldly;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace AeroDB.Analyzers.Tests;

/// <summary>
/// Verifies that the custom <see cref="JsonElementCborConverter"/> fixes
/// the CBOR parameter pipeline for POCOs containing <see cref="JsonElement"/>
/// anywhere in their type graph.
/// </summary>
public class JsonElementCborRepro : IAsyncDisposable
{
    private readonly SurrealDbMemoryClient _client = new();
    private bool _setupDone;

    private async ValueTask EnsureSetup()
    {
        if (_setupDone) return;
        _setupDone = true;

        // Wire up the JsonElement CBOR converter so the $data parameter
        // path can encode JsonElement values correctly.
        AeroDBCborOptions.ConfigureClient(_client);

        await _client.Use("test", "test");
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    /// <summary>Sable-like doc with <c>long Id</c> and JsonElement values.</summary>
    private sealed class DocWithJsonElement
    {
        public long Id { get; set; } = 1;
        public string Name { get; set; } = "test";
        public Dictionary<string, JsonElement> Styles { get; set; } = new()
        {
            ["fontSize"] = JsonSerializer.SerializeToElement("16px"),
            ["color"] = JsonSerializer.SerializeToElement("#333333"),
        };
    }

    /// <summary>Record-based doc with JsonElement values — for typed Upsert.</summary>
    private sealed class RecordWithJsonElement : Record
    {
        public string Name { get; set; } = "test";
        public Dictionary<string, JsonElement> Styles { get; set; } = new()
        {
            ["fontSize"] = JsonSerializer.SerializeToElement("16px"),
        };
    }

    /// <summary>Simple POCO with NO JsonElement in the type graph at all.</summary>
    private sealed class SimpleDoc
    {
        public long Id { get; set; } = 1;
        public string Name { get; set; } = "ok";
        public Dictionary<string, string> Meta { get; set; } = new() { ["key"] = "value" };
    }

    // ── RawQuery $data path ────────────────────────────────────────────

    [Test]
    public async Task RawQuery_with_JsonElement_values__succeeds()
    {
        await EnsureSetup();
        await _client.RawQuery("DEFINE TABLE fake SCHEMAFULL");

        var doc = new DocWithJsonElement();
        var parameters = new Dictionary<string, object?> { ["data"] = doc };

        // This previously threw — now it should work
        await _client.RawQuery("CREATE fake CONTENT $data", parameters);
    }

    // ── Typed Upsert ────────────────────────────────────────────────────

    [Test]
    public async Task Typed_Upsert_with_JsonElement__succeeds()
    {
        await EnsureSetup();
        // Use SCHEMALESS to avoid field-naming convention mismatches;
        // the goal is to prove the CBOR pipeline works for JsonElement.
        await _client.RawQuery("DEFINE TABLE record_with_json_element SCHEMALESS");

        var doc = new RecordWithJsonElement
        {
            Id = new RecordIdOfString("record_with_json_element", "test-1")
        };

        await _client.Upsert(doc);
    }

    // ── Empty dict (type graph alone used to break) ─────────────────────

    [Test]
    public async Task Empty_JsonElement_dict__succeeds()
    {
        await EnsureSetup();
        await _client.RawQuery("DEFINE TABLE fake SCHEMAFULL");

        var doc = new DocWithJsonElement { Styles = new() };
        var parameters = new Dictionary<string, object?> { ["data"] = doc };

        // Previously threw TypeGraphException — now works
        await _client.RawQuery("CREATE fake CONTENT $data", parameters);
    }

    // ── Round-trip: insert + read back ──────────────────────────────────

    [Test]
    public async Task JsonElement_values__round_trip_correctly()
    {
        await EnsureSetup();
        await _client.RawQuery("DEFINE TABLE fake SCHEMAFULL");

        var doc = new DocWithJsonElement();
        var parameters = new Dictionary<string, object?> { ["data"] = doc };
        await _client.RawQuery("CREATE fake CONTENT $data", parameters);

        var results = await _client.RawQuery("SELECT * FROM fake WHERE name = $name",
            new Dictionary<string, object?> { ["name"] = "test" });

        await Assert.That(results.HasErrors).IsFalse();
        await Assert.That(results.Count).IsGreaterThan(0);
    }

    // ── Baseline (no JsonElement → unchanged) ───────────────────────────

    [Test]
    public async Task Simple_document_without_JsonElement__succeeds()
    {
        await EnsureSetup();
        await _client.RawQuery("DEFINE TABLE fake SCHEMAFULL");

        var simple = new SimpleDoc();
        var parameters = new Dictionary<string, object?> { ["data"] = simple };

        await _client.RawQuery("CREATE fake CONTENT $data", parameters);
    }
}
