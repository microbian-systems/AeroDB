using TUnit.Core;

namespace Dali.Tests;

public class SchemaGapsTests
{
    [Test]
    public void DatabaseSchemaName_is_null_by_default()
    {
        var options = new EventSourcingOptions();
        options.DatabaseSchemaName.ShouldBeNull();
        options.EventsSchemaName.ShouldBeNull();
    }

    [Test]
    public void ForEvents_returns_mt_events()
    {
        var schema = new SchemaOptions();
        schema.ForEvents().ShouldBe("mt_events");
    }

    [Test]
    public void ForStreams_returns_mt_events()
    {
        var schema = new SchemaOptions();
        schema.ForStreams<string>().ShouldBe("mt_events");
    }

    [Test]
    public void ForEventProgression_returns_mt_projection_progress()
    {
        var schema = new SchemaOptions();
        schema.ForEventProgression().ShouldBe("mt_projection_progress");
    }

    [Test]
    public void EventsTableName_is_mt_events()
    {
        var schema = new SchemaOptions();
        schema.EventsTableName.ShouldBe("mt_events");
    }

    [Test]
    public void ProjectionProgressTableName_is_mt_projection_progress()
    {
        var schema = new SchemaOptions();
        schema.ProjectionProgressTableName.ShouldBe("mt_projection_progress");
    }

    [Test]
    public void ForStreams_returns_same_as_ForEvents()
    {
        var schema = new SchemaOptions();
        schema.ForStreams<int>().ShouldBe(schema.ForEvents());
    }
}
