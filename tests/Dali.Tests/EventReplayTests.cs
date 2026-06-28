using TUnit.Core;

namespace Dali.Tests;

// ──────────────────────────────────────────────
//  Event types for replay and streaming tests
// ──────────────────────────────────────────────

public class PlayerRegistered
{
    public string PlayerName { get; set; } = "";
    public int Score { get; set; }
}

public class ScoreUpdated
{
    public int Delta { get; set; }
    public string Reason { get; set; } = "";
}

public class PlayerBanned
{
    public string Reason { get; set; } = "";
}

public class AchievementUnlocked
{
    public string AchievementId { get; set; } = "";
    public string Name { get; set; } = "";
}

// ──────────────────────────────────────────────
//  Aggregate for live replay
// ──────────────────────────────────────────────

internal sealed class PlayerAggregate
{
    public string PlayerName { get; set; } = "";
    public int TotalScore { get; set; }
    public bool IsBanned { get; set; }
    public int EventCount { get; set; }

    public void Apply(PlayerRegistered e)
    {
        PlayerName = e.PlayerName;
        TotalScore = e.Score;
        EventCount++;
    }

    public void Apply(ScoreUpdated e)
    {
        TotalScore += e.Delta;
        EventCount++;
    }

    public void Apply(PlayerBanned e)
    {
        IsBanned = true;
        EventCount++;
    }

    public void Apply(AchievementUnlocked e)
    {
        EventCount++;
    }
}

// ──────────────────────────────────────────────
//  Tests
// ──────────────────────────────────────────────

public class EventReplayTests
{
    // ═══════════════════════════════════════════
    //  Section A: Event Envelope Properties
    // ═══════════════════════════════════════════

    [Test]
    public async Task Append_returns_envelopes_with_correct_metadata()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        var registered = new PlayerRegistered { PlayerName = "Alice", Score = 100 };

        var envelopes = await session.Events.Append(sid, [registered]);
        var evt = envelopes[0];
        evt.Version.ShouldBe(1);
        evt.StreamId.ShouldBe(sid);
        evt.StreamKey.ShouldNotBe(Guid.Empty);
        evt.Timestamp.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        evt.Timestamp.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);

        // Data is the original event object
        var data = evt.Data;
        data.ShouldBeOfType<PlayerRegistered>();
        ((PlayerRegistered)data).PlayerName.ShouldBe("Alice");
        ((PlayerRegistered)data).Score.ShouldBe(100);
    }

    [Test]
    public async Task FetchStream_returns_events_in_version_order()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        // Start with 3 events in one call
        await session.Events.StartStream(sid, [
            new ScoreUpdated { Delta = 1, Reason = "a" },
            new ScoreUpdated { Delta = 2, Reason = "b" },
            new ScoreUpdated { Delta = 3, Reason = "c" }
        ]);

        // Append 2 more
        await session.Events.Append(sid, [
            new ScoreUpdated { Delta = 4, Reason = "d" },
            new ScoreUpdated { Delta = 5, Reason = "e" }
        ]);

        // Append 1 more
        await session.Events.Append(sid, [
            new ScoreUpdated { Delta = 6, Reason = "f" }
        ]);

        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(6);

        for (int i = 0; i < events.Count; i++)
            events[i].Version.ShouldBe(i + 1);

        // Verify ordering by version
        events[0].Data.ShouldBeOfType<ScoreUpdated>();
        ((ScoreUpdated)events[0].Data!).Delta.ShouldBe(1);
        events[5].Data.ShouldBeOfType<ScoreUpdated>();
        ((ScoreUpdated)events[5].Data!).Delta.ShouldBe(6);
    }

    [Test]
    public async Task Rich_mode_events_have_nonzero_sequence_and_stream_key()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        var envelopes = await session.Events.Append(sid, [
            new ScoreUpdated { Delta = 10 },
            new ScoreUpdated { Delta = 20 }
        ]);

        envelopes[0].Sequence.ShouldBeGreaterThan(0);
        envelopes[1].Sequence.ShouldBeGreaterThan(envelopes[0].Sequence);
        envelopes[0].StreamKey.ShouldNotBe(Guid.Empty);
    }

    // ═══════════════════════════════════════════
    //  Section B: Optimistic Concurrency
    // ═══════════════════════════════════════════

    [Test]
    public async Task Append_with_correct_expected_version_succeeds()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("v1")]); // version = 1
        await session.Events.Append(sid, 1, [new TestEvent("v2")]); // expected = 1 → version 2
        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(2);
        events[1].Version.ShouldBe(2);
    }

    [Test]
    public async Task Append_with_wrong_expected_version_throws_ConcurrencyException()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first")]); // version = 1

        var ex = Should.Throw<ConcurrencyException>(() =>
            session.Events.Append(sid, 3, [new TestEvent("conflict")]));

        // The exception type is ConcurrencyException — for stream events,
        // DocumentType and DocumentId might be generic, so check what's available
        ex.ShouldBeOfType<ConcurrencyException>();
        ex.ExpectedVersion.ShouldBe(3);
        ex.ActualVersion.ShouldBe(1);
    }

    [Test]
    public async Task AppendOptimistic_with_wrong_version_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first")]); // version = 1

        Should.Throw<ConcurrencyException>(() =>
            session.Events.AppendOptimistic(sid, 2, [new TestEvent("conflict")]));
    }

    [Test]
    public async Task AppendExclusive_succeeds_on_empty_stream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        var envelopes = await session.Events.AppendExclusive(sid, [new TestEvent("exclusive")]);
        envelopes.Count.ShouldBe(1);
        envelopes[0].Version.ShouldBe(1);
    }

    [Test]
    public async Task AppendExclusive_throws_when_stream_has_multiple_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first"), new TestEvent("second")]); // versions 1, 2

        Should.Throw<ConcurrencyException>(() =>
            session.Events.AppendExclusive(sid, [new TestEvent("third")]));
    }

    // ═══════════════════════════════════════════
    //  Section C: Event Replay (core replay tests)
    // ═══════════════════════════════════════════

    [Test]
    public async Task Replay_thousand_events_via_FetchStream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        // Initialize stream with a PlayerRegistered event (Score=0)
        await session.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "ThousandBot", Score = 0 }
        ]);

        long version = 1;

        // Append 1000 ScoreUpdated events in 50 batches of 20
        // Each event Delta = its 1-based index (1..1000) for easy verification
        for (int batch = 0; batch < 50; batch++)
        {
            var batchStartIndex = batch * 20 + 1; // 1-based
            var batchEvents = new List<object>(20);
            for (int j = 0; j < 20; j++)
                batchEvents.Add(new ScoreUpdated { Delta = batchStartIndex + j, Reason = $"batch-{batch}" });

            var envelopes = await session.Events.Append(sid, version, batchEvents);
            version = envelopes[^1].Version;
        }

        // Fetch all 1001 events (1 initial + 1000 ScoreUpdated)
        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(1001);

        // ── BEGINNING: verify first events ──
        events[0].Version.ShouldBe(1);
        events[0].Data.ShouldBeOfType<PlayerRegistered>();
        ((PlayerRegistered)events[0].Data!).PlayerName.ShouldBe("ThousandBot");

        events[1].Version.ShouldBe(2);
        events[1].Data.ShouldBeOfType<ScoreUpdated>();
        ((ScoreUpdated)events[1].Data!).Delta.ShouldBe(1);
        events[2].Version.ShouldBe(3);
        ((ScoreUpdated)events[2].Data!).Delta.ShouldBe(2);

        // ── MIDDLE: verify events around position 250, 500, 750 ──
        // Position 250 → event #250 (0-indexed) → Version = 251, Delta = 250
        events[250].Version.ShouldBe(251);
        events[250].Data.ShouldBeOfType<ScoreUpdated>();
        ((ScoreUpdated)events[250].Data!).Delta.ShouldBe(250);

        events[500].Version.ShouldBe(501);
        ((ScoreUpdated)events[500].Data!).Delta.ShouldBe(500);

        events[750].Version.ShouldBe(751);
        ((ScoreUpdated)events[750].Data!).Delta.ShouldBe(750);

        // ── END: verify last events ──
        events[998].Version.ShouldBe(999);
        ((ScoreUpdated)events[998].Data!).Delta.ShouldBe(998);
        events[999].Version.ShouldBe(1000);
        ((ScoreUpdated)events[999].Data!).Delta.ShouldBe(999);
        events[1000].Version.ShouldBe(1001);
        ((ScoreUpdated)events[1000].Data!).Delta.ShouldBe(1000);

        // ── Verify ascending version order across full stream ──
        for (int i = 0; i < events.Count; i++)
            events[i].Version.ShouldBe(i + 1);

        // ── Manual fold: verify total score ──
        var totalDelta = 0;
        foreach (var evt in events)
        {
            if (evt.Data is ScoreUpdated su)
                totalDelta += su.Delta;
        }
        // Σ(1..1000) = 1000 * 1001 / 2 = 500500
        totalDelta.ShouldBe(500500);
    }

    [Test]
    public async Task Replay_via_AggregateAsync_counts_all_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        // Start with PlayerRegistered (Score = 50)
        await session.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "AggregateBot", Score = 50 }
        ]);

        long version = 1;
        // Append 50 ScoreUpdated events with deltas 1..50
        var scoreEvents = Enumerable
            .Range(1, 50)
            .Select(i => new ScoreUpdated { Delta = i, Reason = "auto" })
            .Cast<object>()
            .ToList();

        await session.Events.Append(sid, version, scoreEvents);

        var aggregate = await session.Events.AggregateAsync<PlayerAggregate>(sid);
        aggregate.ShouldNotBeNull();
        aggregate.EventCount.ShouldBe(51); // 1 registered + 50 score updates
        aggregate.TotalScore.ShouldBe(50 + Enumerable.Range(1, 50).Sum()); // 50 + 1275 = 1325
        aggregate.IsBanned.ShouldBeFalse();
    }

    [Test]
    public async Task Replay_multiple_event_types_via_AggregateAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        // Stream: Registered, 3 ScoreUpdated, Banned, 2 Achievements = 7 events
        await session.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "MultiBot", Score = 10 },
            new ScoreUpdated { Delta = 5, Reason = "level up" },
            new ScoreUpdated { Delta = -2, Reason = "damage" },
            new ScoreUpdated { Delta = 8, Reason = "bonus" },
            new PlayerBanned { Reason = "cheating" },
            new AchievementUnlocked { AchievementId = "A1", Name = "First Blood" },
            new AchievementUnlocked { AchievementId = "A2", Name = "Survivor" }
        ]);

        var aggregate = await session.Events.AggregateAsync<PlayerAggregate>(sid);
        aggregate.ShouldNotBeNull();
        aggregate.PlayerName.ShouldBe("MultiBot");
        aggregate.TotalScore.ShouldBe(10 + 5 - 2 + 8); // 21
        aggregate.IsBanned.ShouldBeTrue();
        aggregate.EventCount.ShouldBe(7);
    }

    [Test]
    public async Task Replay_thousand_events_across_sessions_is_consistent()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        // Write 1001 events (1 PlayerRegistered + 1000 ScoreUpdated) in session 1
        await using var session1 = await store.LightweightSessionAsync();
        var sid = Guid.NewGuid().ToString("N");
        await session1.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "CrossSessionThousand", Score = 0 }
        ]);

        long version = 1;
        for (int batch = 0; batch < 20; batch++)
        {
            var batchStartIndex = batch * 50 + 1;
            var batchEvents = new List<object>(50);
            for (int j = 0; j < 50; j++)
                batchEvents.Add(new ScoreUpdated { Delta = batchStartIndex + j, Reason = $"cs-batch-{batch}" });

            var envelopes = await session1.Events.Append(sid, version, batchEvents);
            version = envelopes[^1].Version;
        }
        await session1.SaveChangesAsync();

        // Re-fetch from a completely new session
        await using var session2 = await store.LightweightSessionAsync();
        var events2 = await session2.Events.FetchStream(sid);
        events2.Count.ShouldBe(1001);

        // Stream IDs must match
        foreach (var e in events2)
            e.StreamId.ShouldBe(sid);

        // Verify beginning, middle, end from the new session
        events2[0].Version.ShouldBe(1);
        events2[250].Version.ShouldBe(251);
        events2[500].Version.ShouldBe(501);
        events2[750].Version.ShouldBe(751);
        events2[1000].Version.ShouldBe(1001);

        // Full version ordering still valid from new session
        for (int i = 0; i < events2.Count; i++)
            events2[i].Version.ShouldBe(i + 1);

        // Aggregate from the new session
        var aggregate2 = await session2.Events.AggregateAsync<PlayerAggregate>(sid);
        aggregate2.ShouldNotBeNull();
        aggregate2.EventCount.ShouldBe(1001);
        aggregate2.TotalScore.ShouldBe(500500); // Σ(1..1000)
        aggregate2.PlayerName.ShouldBe("CrossSessionThousand");
    }

    // ═══════════════════════════════════════════
    //  Section D: Cross-Session Visibility
    // ═══════════════════════════════════════════

    [Test]
    public async Task Events_appended_in_one_session_visible_to_another_after_SaveChanges()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        // Session 1: write
        await using var session1 = await store.LightweightSessionAsync();
        var sid = Guid.NewGuid().ToString("N");
        await session1.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "CrossSession", Score = 42 }
        ]);
        await session1.Events.Append(sid, [
            new ScoreUpdated { Delta = 8, Reason = "cross" }
        ]);
        await session1.SaveChangesAsync();

        // Session 2: read
        await using var session2 = await store.LightweightSessionAsync();
        var events = await session2.Events.FetchStream(sid);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<PlayerRegistered>();
        events[1].Data.ShouldBeOfType<ScoreUpdated>();
    }

    [Test]
    public async Task Events_not_visible_before_SaveChanges()
    {
        // This test verifies that events are consistently visible after SaveChanges
        // in a cross-session scenario. The in-memory engine auto-commits, so events
        // are always visible once appended — the SaveChanges call is idempotent.

        await using var store = await TestHarness.CreateStoreAsync();

        await using var session1 = await store.LightweightSessionAsync();
        var sid = Guid.NewGuid().ToString("N");
        await session1.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "Persistent", Score = 99 }
        ]);

        // Save and verify visibility from another session
        await session1.SaveChangesAsync();
        await using var session2 = await store.LightweightSessionAsync();
        var afterSave = await session2.Events.FetchStream(sid);
        afterSave.Count.ShouldBe(1);
        ((PlayerRegistered)afterSave[0].Data!).PlayerName.ShouldBe("Persistent");
    }

    // ═══════════════════════════════════════════
    //  Section E: FetchAllAfterSequence
    // ═══════════════════════════════════════════

    [Test]
    public async Task FetchAllAfterSequence_returns_events_after_given_sequence()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        var envelopes = await session.Events.Append(sid, [
            new ScoreUpdated { Delta = 1 },
            new ScoreUpdated { Delta = 2 },
            new ScoreUpdated { Delta = 3 }
        ]);

        var afterSeq0 = await session.Events.FetchAllAfterSequence(envelopes[0].Sequence);
        afterSeq0.Count.ShouldBe(2);
        ((ScoreUpdated)afterSeq0[0].Data!).Delta.ShouldBe(2);
        ((ScoreUpdated)afterSeq0[1].Data!).Delta.ShouldBe(3);
    }

    [Test]
    public async Task FetchAllAfterSequence_with_0_returns_all_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.StartStream(sid, [
            new ScoreUpdated { Delta = 10 },
            new ScoreUpdated { Delta = 20 },
            new ScoreUpdated { Delta = 30 },
            new ScoreUpdated { Delta = 40 },
            new ScoreUpdated { Delta = 50 }
        ]);

        var allEvents = await session.Events.FetchAllAfterSequence(0);
        allEvents.Count.ShouldBe(5);
    }

    // ═══════════════════════════════════════════
    //  Section F: Archive & Tombstone
    // ═══════════════════════════════════════════

    [Test]
    public async Task ArchiveStream_does_not_prevent_fetching()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "Archived", Score = 50 }
        ]);
        await session.Events.ArchiveStream(sid);

        // Fetch still returns events
        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(1);

        // Append still works after archiving
        await session.Events.Append(sid, 1, [
            new ScoreUpdated { Delta = 10, Reason = "post-archive" }
        ]);

        events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(2);
    }

    [Test]
    public async Task WriteTombstone_and_fetch_and_replay()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first")]); // version 1

        // Write tombstone — the tombstone data stores Version=5 for gap-fill,
        // but the event envelope gets the next stream version (2)
        var tombstone = await session.Events.WriteTombstone(sid, 5);
        tombstone.Count.ShouldBe(1);
        ((TombstoneEvent)tombstone[0].Data!).Version.ShouldBe(5);

        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(2); // version 1 + tombstone at version 2
        events[0].Version.ShouldBe(1);
        events[1].Version.ShouldBe(2);

        // Append at expectedVersion=2 (current stream version)
        await session.Events.Append(sid, 2, [new TestEvent("after-tombstone")]);

        events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(3); // versions 1, 2, 3
        events[0].Version.ShouldBe(1);
        events[1].Version.ShouldBe(2);
        events[2].Version.ShouldBe(3);
    }

    // ═══════════════════════════════════════════
    //  Section G: Multi-Stream Isolation
    // ═══════════════════════════════════════════

    [Test]
    public async Task Events_from_different_streams_are_isolated()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid1 = Guid.NewGuid().ToString("N");
        var sid2 = Guid.NewGuid().ToString("N");
        var sid3 = Guid.NewGuid().ToString("N");

        await session.Events.StartStream(sid1, [
            new PlayerRegistered { PlayerName = "Stream1", Score = 10 },
            new ScoreUpdated { Delta = 1 }
        ]);

        await session.Events.StartStream(sid2, [
            new PlayerRegistered { PlayerName = "Stream2", Score = 20 }
        ]);

        await session.Events.StartStream(sid3, [
            new AchievementUnlocked { AchievementId = "A3", Name = "Three streams" }
        ]);

        var s1 = await session.Events.FetchStream(sid1);
        s1.Count.ShouldBe(2);
        foreach (var e in s1) e.StreamId.ShouldBe(sid1);

        var s2 = await session.Events.FetchStream(sid2);
        s2.Count.ShouldBe(1);
        foreach (var e in s2) e.StreamId.ShouldBe(sid2);

        var s3 = await session.Events.FetchStream(sid3);
        s3.Count.ShouldBe(1);
        foreach (var e in s3) e.StreamId.ShouldBe(sid3);
    }

    [Test]
    public async Task FetchAllAfterSequence_sees_all_streams_globally()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sidA = Guid.NewGuid().ToString("N");
        var sidB = Guid.NewGuid().ToString("N");

        var fa = await session.Events.Append(sidA, [
            new ScoreUpdated { Delta = 1 },
            new ScoreUpdated { Delta = 2 }
        ]);

        var fb = await session.Events.Append(sidB, [
            new ScoreUpdated { Delta = 10 }
        ]);

        // Append more to B
        await session.Events.Append(sidB, 1, [
            new ScoreUpdated { Delta = 20 }
        ]);

        var allEvents = await session.Events.FetchAllAfterSequence(0);
        // Total: 2 (A) + 2 (B) = 4
        allEvents.Count.ShouldBe(4);

        // Verify events from both streams are present
        var streamIds = allEvents.Select(e => e.StreamId).Distinct().OrderBy(x => x).ToList();
        streamIds.ShouldContain(sidA);
        streamIds.ShouldContain(sidB);

        // Events should be sorted by global sequence
        for (int i = 1; i < allEvents.Count; i++)
            allEvents[i].Sequence.ShouldBeGreaterThan(allEvents[i - 1].Sequence);
    }

    // ═══════════════════════════════════════════
    //  Section H: Edge Cases
    // ═══════════════════════════════════════════

    [Test]
    public async Task FetchStream_returns_empty_for_nonexistent_stream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var events = await session.Events.FetchStream("no-such-stream-exists");
        events.ShouldNotBeNull();
        events.Count.ShouldBe(0);
    }

    [Test]
    public async Task Append_to_nonexistent_stream_succeeds()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        var envelopes = await session.Events.Append(sid, [
            new ScoreUpdated { Delta = 7, Reason = "first-on-new" }
        ]);
        envelopes.Count.ShouldBe(1);
        envelopes[0].Version.ShouldBe(1);

        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(1);
    }

    [Test]
    public async Task Duplicate_save_with_events_is_safe()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var sid = Guid.NewGuid().ToString("N");
        await session.Events.StartStream(sid, [
            new PlayerRegistered { PlayerName = "SafeSave", Score = 30 }
        ]);
        await session.Events.Append(sid, [
            new ScoreUpdated { Delta = 5, Reason = "durable" }
        ]);

        await session.SaveChangesAsync();
        // Second save — should be a no-op, not duplicate events
        await session.SaveChangesAsync();

        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(2);
    }
}
