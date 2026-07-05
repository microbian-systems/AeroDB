// REMOVED: JasperFx.Events.Projections, Marten, Marten.Events.Aggregation, Marten.Events.Projections, Marten.Testing.Harness
// AeroDB is global using from GlobalUsings.cs
using SurrealDb.Embedded.InMemory;

namespace DocSamples;

#region sample_sample-events

public sealed record ArrivedAtLocation(Guid QuestId, int Day, string Location);

public sealed record MembersJoined(Guid QuestId, int Day, string Location, string[] Members);

public sealed record QuestStarted(Guid QuestId, string Name);

public sealed record QuestEnded(Guid QuestId, string Name);

public sealed record MembersDeparted(Guid QuestId, int Day, string Location, string[] Members);

public sealed record MembersEscaped(Guid QuestId, string Location, string[] Members);


#endregion


#region sample_questparty

public sealed record QuestParty(Guid Id, List<string> Members)
{
    // Parameterless constructor for AeroDB snapshot/live-aggregation projections
    public QuestParty() : this(default, []) { }

    // These methods take in events and update the QuestParty
    public static QuestParty Create(QuestStarted started) => new(started.QuestId, []);
    public static QuestParty Apply(MembersJoined joined, QuestParty party) =>
        party with
        {
            Members = party.Members.Union(joined.Members).ToList()
        };

    public static QuestParty Apply(MembersDeparted departed, QuestParty party) =>
        party with
        {
            Members = party.Members.Where(x => !departed.Members.Contains(x)).ToList()
        };

    public static QuestParty Apply(MembersEscaped escaped, QuestParty party) =>
        party with
        {
            Members = party.Members.Where(x => !escaped.Members.Contains(x)).ToList()
        };
}

#endregion

#region sample_addmembers_command_handler

public record AddMembers(Guid Id, int Day, string Location, string[] Members);

public static class AddMembersHandler
{
    public static async Task HandleAsync(AddMembers command, IDocumentSession session)
    {
        // Fetch the current state of the quest
        var quest = await session.Events.FetchForWritingAsync<QuestParty>(command.Id.ToString());
        if (quest.Aggregate == null)
        {
            // Bad quest id, do nothing in this sample case
        }

        var newMembers = command.Members.Where(x => !quest.Aggregate.Members.Contains(x)).ToArray();

        if (!newMembers.Any())
        {
            return;
        }

        quest.AppendOne(new MembersJoined(command.Id, command.Day, command.Location, newMembers));
        await session.SaveChangesAsync();
    }
}

#endregion


#region sample_quest
public sealed record Quest(Guid Id, List<string> Members, List<string> Slayed, string Name, bool isFinished);

public sealed partial class QuestProjection: SingleStreamProjection<Quest, Guid>
{
    public override Type[] EventTypes =>
        [typeof(QuestStarted), typeof(MembersJoined), typeof(MembersDeparted), typeof(MembersEscaped), typeof(QuestEnded)];

    public static Quest Create(QuestStarted started) => new(started.QuestId, [], [], started.Name, false);
    public static Quest Apply(MembersJoined joined, Quest party) =>
        party with
        {
            Members = party.Members.Union(joined.Members).ToList()
        };

    public static Quest Apply(MembersDeparted departed, Quest party) =>
        party with
        {
            Members = party.Members.Where(x => !departed.Members.Contains(x)).ToList()
        };

    public static Quest Apply(MembersEscaped escaped, Quest party) =>
        party with
        {
            Members = party.Members.Where(x => !escaped.Members.Contains(x)).ToList()
        };

    public static Quest Apply(QuestEnded ended, Quest party) =>
        party with { isFinished = true };

}

#endregion


public class EventSourcingQuickstart
{
    [Fact]
    public async Task capture_events()
    {
        #region sample_event-store-quickstart

        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.Namespace = "docsamples";
            o.Database = "docsamples";
        });
        await store.InitializeAsync();

        var questId = Guid.NewGuid();

        await using var session = await store.LightweightSessionAsync();
        var started = new QuestStarted(questId, "Destroy the One Ring");
        var joined1 = new MembersJoined(questId,1, "Hobbiton", ["Frodo", "Sam"]);

        // Start a brand new stream and commit the new events as
        // part of a transaction
        session.Events.StartStream(questId.ToString(), new object[] { started, joined1 });

        // Append more events to the same stream
        var joined2 = new MembersJoined(questId,3, "Buckland", ["Merry", "Pippen"]);
        var joined3 = new MembersJoined(questId,10, "Bree", ["Aragorn"]);
        var arrived = new ArrivedAtLocation(questId, 15, "Rivendell");
        session.Events.Append(questId.ToString(), new object[] { joined2, joined3, arrived });

        // Save the pending changes to db
        await session.SaveChangesAsync();

        #endregion

        #region sample_events-aggregate-on-the-fly

        await using var session2 = await store.LightweightSessionAsync();
        // questId is the id of the stream
        var party = await session2.Events.AggregateStreamAsync<QuestParty>(questId.ToString());

        var party_at_version_3 = await session2.Events
            .AggregateStreamAsync<QuestParty>(questId.ToString(), 3);

        var party_yesterday = await session2.Events
            .AggregateStreamAsync<QuestParty>(questId.ToString(), timestamp: DateTime.UtcNow.AddDays(-1));

        #endregion

    }
    [Fact]
    public async Task quest_projection()
    {
        #region sample_adding-quest-projection
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.Namespace = "docsamples";
            o.Database = "docsamples";
            o.Schema.For<Quest>().Identity(x => x.Id);
            o.Projections.Add<QuestProjection>(ProjectionLifecycle.Inline);
        });
        await store.InitializeAsync();
        #endregion

        var questId = Guid.NewGuid();

        #region sample_querying-quest-projection
        await using var session = await store.LightweightSessionAsync();

        var started = new QuestStarted(questId, "Destroy the One Ring");
        var joined1 = new MembersJoined(questId, 1, "Hobbiton", ["Frodo", "Sam"]);

        session.Events.StartStream(questId.ToString(), new object[] { started, joined1 });
        await session.SaveChangesAsync();

        // we can now query the quest state like any other Marten document
        var questState = await session.LoadAsync<Quest>(questId);

        var finishedQuests = await session.Query<Quest>().Where(x => x.isFinished).ToListAsync();

        #endregion

    }
}
