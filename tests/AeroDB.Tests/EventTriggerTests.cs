using System.Reflection;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public class EventTriggerTests
{
    [Test]
    public async Task EventTriggerDefinition_Defaults()
    {
        var trigger = new EventTriggerDefinition();

        trigger.Name.ShouldBeEmpty();
        trigger.Table.ShouldBeEmpty();
        trigger.Action.ShouldBeEmpty();
        trigger.WhenCondition.ShouldBeNull();
        trigger.Async.ShouldBeFalse();
        trigger.Retry.ShouldBeNull();
        trigger.MaxDepth.ShouldBeNull();
    }

    [Test]
    public async Task EventTriggerDefinition_CanSetProperties()
    {
        var trigger = new EventTriggerDefinition
        {
            Name = "test_trigger",
            Table = "user",
            Action = "CREATE audit SET event = $event",
            WhenCondition = "$event = 'CREATE'",
            Async = true,
            Retry = 3,
            MaxDepth = 5
        };

        trigger.Name.ShouldBe("test_trigger");
        trigger.Table.ShouldBe("user");
        trigger.Action.ShouldBe("CREATE audit SET event = $event");
        trigger.WhenCondition.ShouldBe("$event = 'CREATE'");
        trigger.Async.ShouldBeTrue();
        trigger.Retry.ShouldBe(3);
        trigger.MaxDepth.ShouldBe(5);
    }

    [Test]
    public async Task EventTriggerOptions_AddTrigger()
    {
        var options = new EventTriggerOptions();
        options.AddTrigger("test", "user", "CREATE audit SET event = $event");

        options.Triggers.Count.ShouldBe(1);
        options.Triggers[0].Name.ShouldBe("test");
        options.Triggers[0].Table.ShouldBe("user");
        options.Triggers[0].Action.ShouldBe("CREATE audit SET event = $event");
    }

    [Test]
    public async Task EventTriggerOptions_AddTrigger_Generic_T()
    {
        var options = new EventTriggerOptions();
        options.AddTrigger<Person>("person_created",
            "CREATE audit SET event = $event, record_id = $after.id",
            whenCondition: "$event = 'CREATE'");

        options.Triggers.Count.ShouldBe(1);
        options.Triggers[0].Table.ShouldBe("person");
        options.Triggers[0].Name.ShouldBe("person_created");
        options.Triggers[0].WhenCondition.ShouldBe("$event = 'CREATE'");
    }

    [Test]
    public async Task EventTriggerOptions_AddTrigger_Generic_WithAllParameters()
    {
        var options = new EventTriggerOptions();
        options.AddTrigger<Person>("sync_audit",
            "CREATE audit SET data = $after",
            whenCondition: "$event = 'UPDATE'",
            async: false,
            retry: 2,
            maxDepth: 3);

        var trigger = options.Triggers[0];
        trigger.Name.ShouldBe("sync_audit");
        trigger.Table.ShouldBe("person");
        trigger.Action.ShouldBe("CREATE audit SET data = $after");
        trigger.WhenCondition.ShouldBe("$event = 'UPDATE'");
        trigger.Async.ShouldBeFalse();
        trigger.Retry.ShouldBe(2);
        trigger.MaxDepth.ShouldBe(3);
    }

    [Test]
    public async Task EventTriggerOptions_AutoCreateDefault()
    {
        var options = new EventTriggerOptions();
        options.AutoCreateTriggers.ShouldBeTrue();
    }

    [Test]
    public async Task EventTriggerOptions_ChainedCalls()
    {
        var options = new EventTriggerOptions();
        options.AddTrigger("a", "t1", "action1")
               .AddTrigger("b", "t2", "action2");

        options.Triggers.Count.ShouldBe(2);
        options.Triggers[0].Name.ShouldBe("a");
        options.Triggers[1].Name.ShouldBe("b");
    }

    [Test]
    public async Task EventTriggerOptions_CanSetAutoCreateFalse()
    {
        var options = new EventTriggerOptions
        {
            AutoCreateTriggers = false
        };
        options.AutoCreateTriggers.ShouldBeFalse();
    }

    [Test]
    public async Task EventTriggerManager_BuildsCorrectSurql()
    {
        var trigger = new EventTriggerDefinition
        {
            Name = "user_created",
            Table = "user",
            Action = "CREATE audit SET event = $event, table_name = 'user'",
            WhenCondition = "$event = 'CREATE'"
        };

        var method = typeof(EventTriggerManager).GetMethod("BuildDefineEventSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [trigger])!;

        surql.ShouldContain("DEFINE EVENT user_created ON TABLE user");
        surql.ShouldContain("WHEN $event = 'CREATE'");
        surql.ShouldContain("THEN (CREATE audit SET event = $event, table_name = 'user')");
    }

    [Test]
    public async Task EventTriggerManager_BuildsAsyncTrigger()
    {
        var trigger = new EventTriggerDefinition
        {
            Name = "slow_job",
            Table = "publication",
            Action = "CREATE log SET data = $after",
            Async = true,
            Retry = 3,
            MaxDepth = 5
        };

        var method = typeof(EventTriggerManager).GetMethod("BuildDefineEventSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [trigger])!;

        surql.ShouldContain("ASYNC");
        surql.ShouldContain("RETRY 3");
        surql.ShouldContain("MAXDEPTH 5");
    }

    [Test]
    public async Task EventTriggerManager_BuildsTriggerWithoutWhen()
    {
        var trigger = new EventTriggerDefinition
        {
            Name = "always_fire",
            Table = "data",
            Action = "CREATE log SET data = $after"
        };

        var method = typeof(EventTriggerManager).GetMethod("BuildDefineEventSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [trigger])!;

        surql.ShouldContain("DEFINE EVENT always_fire ON TABLE data");
        surql.ShouldNotContain("WHEN");
        surql.ShouldContain("THEN (CREATE log SET data = $after)");
    }

    [Test]
    public async Task EventTriggerManager_BuildsSurql_AsyncWithoutRetry()
    {
        var trigger = new EventTriggerDefinition
        {
            Name = "fire_and_forget",
            Table = "event",
            Action = "CREATE log SET msg = $after",
            Async = true
        };

        var method = typeof(EventTriggerManager).GetMethod("BuildDefineEventSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [trigger])!;

        surql.ShouldContain("ASYNC");
        surql.ShouldNotContain("RETRY");
        surql.ShouldNotContain("MAXDEPTH");
    }

    [Test]
    public async Task EventTriggerManager_RemoveTrigger_BuildsCorrectSurql()
    {
        var surql = (string)typeof(EventTriggerManager)
            .GetMethod("BuildRemoveEventSurql", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["old_trigger", "user"])!;

        surql.ShouldBe("REMOVE EVENT old_trigger ON TABLE user;");
    }

    [Test]
    public async Task EventTriggerManager_AlterTrigger_BuildsCorrectSurql()
    {
        var trigger = new EventTriggerDefinition
        {
            Name = "alter_test",
            Table = "person",
            Action = "CREATE log SET event = 'modified'"
        };

        var method = typeof(EventTriggerManager).GetMethod("BuildDefineEventSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [trigger])!;

        // The AlterTriggerAsync method replaces "DEFINE EVENT" with "ALTER EVENT"
        var alterSurql = surql.Replace("DEFINE EVENT", "ALTER EVENT", StringComparison.Ordinal);
        alterSurql.ShouldStartWith("ALTER EVENT");
        alterSurql.ShouldNotStartWith("DEFINE EVENT");
    }

    [Test]
    public async Task EventTriggerOptions_StoredInEventSourcingOptions()
    {
        var options = new StoreOptions();
        options.Events.Triggers.AddTrigger("evt1", "my_table", "CREATE log SET x = 1");

        options.Events.Triggers.Triggers.Count.ShouldBe(1);
        options.Events.Triggers.Triggers[0].Name.ShouldBe("evt1");
    }
}
