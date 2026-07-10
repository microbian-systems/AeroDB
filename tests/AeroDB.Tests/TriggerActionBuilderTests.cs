using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

// ──────────────────────────────────────────────
// Test entities for trigger action builder tests
// ──────────────────────────────────────────────

public class AuditLog : SurrealDb.Net.Models.Record
{
    public string Event { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Details { get; set; } = "";
}

public class OrderItem : SurrealDb.Net.Models.Record
{
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
}

public class Cart : SurrealDb.Net.Models.Record
{
    public string SessionId { get; set; } = "";
}

/// <summary>
/// Tests for TriggerActionBuilder (Phase 4).
/// </summary>
public class TriggerActionBuilderTests
{
    // ════════════════════════════════════════════════════════════
    //  Section 1 — CREATE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Create_WithSet_SingleField()
    {
        var builder = new TriggerActionBuilder();
        builder.Create<AuditLog>().Set(x => x.Event, "$event").End();

        builder.Build().ShouldBe("CREATE audit_log SET Event = $event");
    }

    [Test]
    public async Task Create_WithSet_MultipleFields()
    {
        var builder = new TriggerActionBuilder();
        builder.Create<AuditLog>()
            .Set(x => x.Event, "$event")
            .Set(x => x.UserId, "$session.user_id")
            .End();

        builder.Build().ShouldBe("CREATE audit_log SET Event = $event, UserId = $session.user_id");
    }

    [Test]
    public async Task Create_WithContent()
    {
        var builder = new TriggerActionBuilder();
        builder.Create<AuditLog>().Content(new { Event = "$event", UserId = "$session.user_id" });

        var result = builder.Build();
        result.ShouldContain("CREATE audit_log CONTENT {");
        result.ShouldContain("\"event\"");
        result.ShouldContain("\"user_id\"");
    }

    [Test]
    public async Task Create_WithSet_ThenEnd_ReturnsBuilder()
    {
        var root = new TriggerActionBuilder();
        var result = root.Create<AuditLog>().Set(x => x.Event, "$event").End();
        result.ShouldBe(root);
    }

    // ════════════════════════════════════════════════════════════
    //  Section 2 — UPDATE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Update_WithSet_AndWhere()
    {
        var builder = new TriggerActionBuilder();
        builder.Update<OrderItem>()
            .Set(x => x.Status, "'cancelled'")
            .Where(x => x.Total < 0)
            .End();

        builder.Build().ShouldBe("UPDATE order_item SET Status = 'cancelled' WHERE total < 0");
    }

    [Test]
    public async Task Update_WithOnlyWhere()
    {
        var builder = new TriggerActionBuilder();
        builder.Update<OrderItem>()
            .Where(x => x.Status == "pending")
            .End();

        builder.Build().ShouldBe("UPDATE order_item WHERE status = 'pending'");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 3 — DELETE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Delete_ProducesCorrectSurql()
    {
        var builder = new TriggerActionBuilder();
        builder.Delete<AuditLog>().End();

        builder.Build().ShouldBe("DELETE audit_log");
    }

    [Test]
    public async Task Delete_WithWhere()
    {
        var builder = new TriggerActionBuilder();
        builder.Delete<AuditLog>()
            .Where(x => x.Event == "timeout")
            .End();

        builder.Build().ShouldBe("DELETE audit_log WHERE event = 'timeout'");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 4 — INSERT
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Insert_ProducesCorrectSurql()
    {
        var builder = new TriggerActionBuilder();
        builder.Insert<AuditLog>(new { Event = "'login'", UserId = "$session.user" });

        var result = builder.Build();
        result.ShouldContain("INSERT INTO audit_log CONTENT");
        result.ShouldContain("\"event\"");
        result.ShouldContain("\"user_id\"");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 5 — RELATE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Relate_ProducesCorrectSurql()
    {
        var builder = new TriggerActionBuilder();
        builder.Relate<AuditLog, AuditLog, AuditLog>("$after.id", "$before.id");

        builder.Build().ShouldBe("RELATE audit_log:$after.id->audit_log->audit_log:$before.id");
    }

    [Test]
    public async Task Relate_WithContent()
    {
        var builder = new TriggerActionBuilder();
        builder.Relate<Cart, AuditLog, OrderItem>("$after.id", "$before.id")
            .Content(new { action = "'purchase'" });

        var result = builder.Build();
        result.ShouldContain("RELATE cart:$after.id->audit_log->order_item:$before.id CONTENT");
        result.ShouldContain("\"action\"");  // snake_case naming
    }

    // ════════════════════════════════════════════════════════════
    //  Section 6 — Raw
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Raw_ProducesVerbatimSurql()
    {
        var builder = new TriggerActionBuilder();
        builder.Raw("DELETE FROM audit_log WHERE event = 'cleanup';");

        builder.Build().ShouldBe("DELETE FROM audit_log WHERE event = 'cleanup';");
    }

    [Test]
    public async Task Raw_MixedWithCreate()
    {
        var builder = new TriggerActionBuilder();
        builder
            .Create<AuditLog>().Set(x => x.Event, "$event").End()
            .Raw("; ")
            .Insert<AuditLog>(new { Event = "'logged'" });

        var result = builder.Build();
        result.ShouldContain("CREATE audit_log SET Event = $event");
        result.ShouldContain("INSERT INTO audit_log CONTENT");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 7 — End-to-end with EventTriggerOptions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task AddTrigger_WithActionBuilder_ProducesCorrectTrigger()
    {
        var options = new EventTriggerOptions();

        options.AddTrigger<AuditLog>("audit_on_order",
            b => b.Create<AuditLog>()
                .Set(x => x.Event, "$event")
                .Set(x => x.UserId, "$token.user_id")
                .End(),
            whenCondition: "true",
            async: false);

        options.Triggers.Count.ShouldBe(1);
        var trigger = options.Triggers[0];
        trigger.Name.ShouldBe("audit_on_order");
        trigger.Table.ShouldBe("audit_log");
        trigger.Action.ShouldBe("CREATE audit_log SET Event = $event, UserId = $token.user_id");
        trigger.WhenCondition.ShouldBe("true");
        trigger.Async.ShouldBeFalse();
    }

    [Test]
    public async Task AddTrigger_WithRaw_ProducesCorrectTrigger()
    {
        var options = new EventTriggerOptions();

        options.AddTrigger<OrderItem>("cancel_negative",
            b => b
                .Update<OrderItem>()
                .Set(x => x.Status, "'cancelled'")
                .Where(x => x.Total < 0)
                .End(),
            whenCondition: "true");

        options.Triggers.Count.ShouldBe(1);
        var trigger = options.Triggers[0];
        trigger.Name.ShouldBe("cancel_negative");
        trigger.Action.ShouldBe("UPDATE order_item SET Status = 'cancelled' WHERE total < 0");
    }
}
