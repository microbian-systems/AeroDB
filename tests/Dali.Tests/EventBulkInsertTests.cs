using TUnit.Core;

namespace Dali.Tests;

/// <summary>
/// Simple test event type used by <see cref="EventBulkInsertTests"/>.
/// Serialized as JSON with snake_case by <see cref="EventStore.BulkInsertEventsAsync"/>
/// and deserialized case-insensitively on fetch.
/// </summary>
public class BulkTestEvent
{
    public string Data { get; set; } = "";
}

/// <summary>
/// Tests for <see cref="IEvents.BulkInsertEventsAsync"/> and <see cref="IEvents.FetchAllAfterSequence"/>.
///
/// Note: BulkInsertEventsAsync stores events via raw SQL INSERT (sequence=0, no stream_key).
/// These events cannot be read back via FetchStream (which uses CBOR-based typed deserialization
/// incompatible with raw INSERT storage). Verification uses RawQueryAsync for counting.
/// </summary>
public class EventBulkInsertTests
{


    /// <summary>
    /// Calling BulkInsertEventsAsync with an empty stream list does not throw
    /// and returns 0.
    /// </summary>
    [Test]
    public async Task BulkInsertEventsAsync_EmptyList_DoesNotThrow()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var count = await session.Events.BulkInsertEventsAsync(
            Array.Empty<(string, IEnumerable<object>)>());

        count.ShouldBe(0);
    }

    /// <summary>
    /// Appends 5 events (sequences 1-5), then fetches all events after
    /// the middle event's sequence. Verifies exactly 2 events are returned
    /// and their sequences are strictly greater.
    ///
    /// This test uses the normal Append path (which produces proper sequence numbers),
    /// not BulkInsertEventsAsync (which stores with sequence=0).
    /// </summary>
    [Test]
    public async Task FetchAllAfterSequence_ReturnsEventsAfterIndex()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var streamId = $"seq-test-{Guid.NewGuid():N}";
        await session.Events.Append(streamId,
        [
            new BulkTestEvent { Data = "First" },
            new BulkTestEvent { Data = "Second" },
            new BulkTestEvent { Data = "Third" },
            new BulkTestEvent { Data = "Fourth" },
            new BulkTestEvent { Data = "Fifth" },
        ]);

        var allEvents = await session.Events.FetchStream(streamId);
        allEvents.Count.ShouldBe(5);

        // Sequences start at 1 in a fresh store; index 2 => sequence 3
        var middleSequence = allEvents[2].Sequence;

        var afterSequence = await session.Events.FetchAllAfterSequence(middleSequence);
        afterSequence.Count.ShouldBe(2);
        afterSequence.ShouldAllBe(e => e.Sequence > middleSequence);
    }
}
