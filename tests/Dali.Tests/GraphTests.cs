using System.Reflection;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ──────────────────────────────────────────────
// Helper types for SurrealQL generator tests
// ──────────────────────────────────────────────

public class Team : Record
{
    public string Name { get; set; } = "";
}

public class Project : Record
{
    public string Name { get; set; } = "";
}

// ═══════════════════════════════════════════════
// PART 1: Reflection Tests (no database needed)
// ═══════════════════════════════════════════════

public class GraphReflectionTests
{
    [Test]
    public async Task EdgeRecord_ExtendsRelationRecord()
    {
        typeof(EdgeRecord).BaseType.ShouldBe(typeof(RelationRecord));
    }

    [Test]
    public async Task Knows_IsEdgeRecord()
    {
        typeof(Knows).IsSubclassOf(typeof(EdgeRecord)).ShouldBeTrue();
    }

    [Test]
    public async Task EdgeRecord_HasInProperty()
    {
        var prop = typeof(EdgeRecord).GetProperty("In");
        prop.ShouldNotBeNull();
        prop!.PropertyType.ShouldBe(typeof(RecordId));
    }

    [Test]
    public async Task EdgeRecord_HasOutProperty()
    {
        var prop = typeof(EdgeRecord).GetProperty("Out");
        prop.ShouldNotBeNull();
        prop!.PropertyType.ShouldBe(typeof(RecordId));
    }

    [Test]
    public async Task IGraphQuery_HasOutMethod()
    {
        var type = typeof(IGraphQuery<object>);
        type.GetMethod("Out")?.IsGenericMethod.ShouldBeTrue();
    }

    [Test]
    public async Task IGraphQuery_HasInMethod()
    {
        var type = typeof(IGraphQuery<object>);
        type.GetMethod("In")?.IsGenericMethod.ShouldBeTrue();
    }

    [Test]
    public async Task IGraphQuery_HasBothMethod()
    {
        var type = typeof(IGraphQuery<object>);
        type.GetMethod("Both")?.IsGenericMethod.ShouldBeTrue();
    }

    [Test]
    public async Task IGraphQuery_HasDepthMethod()
    {
        var type = typeof(IGraphQuery<object>);
        var methods = type.GetMethods().Where(m => m.Name == "Depth").ToList();
        methods.Count.ShouldBe(3); // Depth(), Depth(int), Depth(int, int)
    }

    [Test]
    public async Task IGraphQuery_HasShortestPathMethod()
    {
        var method = typeof(IGraphQuery<object>).GetMethod("ShortestPath");
        method.ShouldNotBeNull();
        method!.GetParameters()[0].ParameterType.ShouldBe(typeof(string));
    }

    [Test]
    public async Task IGraphQuery_HasReturnPathMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("ReturnPath").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasCollectAllMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("CollectAll").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasIncludeIntermediateMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("IncludeIntermediate").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasIncludeOriginMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("IncludeOrigin").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasFetchMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("Fetch").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasToListAsyncMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("ToListAsync").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasFirstOrDefaultAsyncMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("FirstOrDefaultAsync").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasToPathListAsyncMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("ToPathListAsync").ShouldNotBeNull();
    }

    [Test]
    public async Task IDocumentSession_HasRelateAsync()
    {
        var type = typeof(IDocumentSession);
        var method = type.GetMethod("RelateAsync");
        method.ShouldNotBeNull();
        method!.IsGenericMethod.ShouldBeTrue();
        var pars = method.GetParameters();
        pars.ShouldContain(p => p.Name == "from");
        pars.ShouldContain(p => p.Name == "to");
    }

    [Test]
    public async Task IDocumentSession_HasUnrelateAsync()
    {
        typeof(IDocumentSession).GetMethod("UnrelateAsync").ShouldNotBeNull();
    }

    [Test]
    public async Task IQuerySession_HasGraphMethod()
    {
        var method = typeof(IQuerySession).GetMethod("Graph");
        method.ShouldNotBeNull();
        method!.IsGenericMethod.ShouldBeTrue();
        method.ReturnType.IsGenericType.ShouldBeTrue();
        method.ReturnType.GetGenericTypeDefinition().ShouldBe(typeof(IGraphQuery<>));
    }

    [Test]
    public async Task GraphNode_HasIdAndTable()
    {
        var idProp = typeof(GraphNode).GetProperty("Id");
        idProp.ShouldNotBeNull();
        idProp!.PropertyType.ShouldBe(typeof(string));

        var tableProp = typeof(GraphNode).GetProperty("Table");
        tableProp.ShouldNotBeNull();
        tableProp!.PropertyType.ShouldBe(typeof(string));
    }

    [Test]
    public async Task GraphPath_HasNodesAndEdges()
    {
        var nodesProp = typeof(GraphPath).GetProperty("Nodes");
        nodesProp.ShouldNotBeNull();
        nodesProp!.PropertyType.ShouldBe(typeof(List<GraphNode>));

        var edgesProp = typeof(GraphPath).GetProperty("Edges");
        edgesProp.ShouldNotBeNull();
        edgesProp!.PropertyType.ShouldBe(typeof(List<GraphEdge>));
    }

    [Test]
    public async Task GraphEdge_HasIdInOut()
    {
        var idProp = typeof(GraphEdge).GetProperty("Id");
        idProp.ShouldNotBeNull();
        idProp!.PropertyType.ShouldBe(typeof(string));

        var inProp = typeof(GraphEdge).GetProperty("In");
        inProp.ShouldNotBeNull();
        inProp!.PropertyType.ShouldBe(typeof(string));

        var outProp = typeof(GraphEdge).GetProperty("Out");
        outProp.ShouldNotBeNull();
        outProp!.PropertyType.ShouldBe(typeof(string));
    }

    [Test]
    public async Task EdgeRecord_IsAbstract()
    {
        typeof(EdgeRecord).IsAbstract.ShouldBeTrue();
    }

    [Test]
    public async Task IGraphQuery_HasOutAnyMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("OutAny").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasInAnyMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("InAny").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasAnyEdgeMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("AnyEdge").ShouldNotBeNull();
    }

    [Test]
    public async Task IGraphQuery_HasCountAsyncMethod()
    {
        typeof(IGraphQuery<object>).GetMethod("CountAsync").ShouldNotBeNull();
    }
}

// ═══════════════════════════════════════════════
// PART 2: Integration Tests (uses embedded engine)
// ═══════════════════════════════════════════════

public class GraphIntegrationTests
{
    private async Task SetupSocialGraphAsync(IDocumentSession session)
    {
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        var charlie = new Person { Name = "Charlie" };

        session.Store(alice);
        session.Store(bob);
        session.Store(charlie);
        await session.SaveChangesAsync();

        // Query back to get populated RecordIds
        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id;
        var bobId = people.First(p => p.Name == "Bob").Id;
        var charlieId = people.First(p => p.Name == "Charlie").Id;

        await session.RelateAsync<Knows>(aliceId!, bobId!, new Knows { Kind = "friend", Since = 2020 }, CancellationToken.None);
        await session.RelateAsync<Knows>(bobId!, charlieId!, new Knows { Kind = "colleague", Since = 2021 }, CancellationToken.None);
    }

    [Test]
    public async Task Graph_SimpleTraversal_ReturnsResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        // Traverse from Alice via knows edge
        var results = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person>("knows")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Graph_OutWithEdgeType_ReturnsCorrectTarget()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        // Verify Alice -> Bob
        var aliceResults = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person>("knows")
            .ToListAsync();

        aliceResults.Count.ShouldBe(1);
        aliceResults[0].Name.ShouldBe("Bob");

        // Verify Bob -> Charlie
        var bobResults = await session.Graph<Person>()
            .Where(p => p.Name == "Bob")
            .Out<Person>("knows")
            .ToListAsync();

        bobResults.Count.ShouldBe(1);
        bobResults[0].Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task Graph_InWithEdgeType_ReturnsCorrectSource()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        // Traverse backwards from Charlie
        var results = await session.Graph<Person>()
            .Where(p => p.Name == "Charlie")
            .In<Person>("knows")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Graph_Both_ReturnsConnectedNodes()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        // Bidirectional from Bob — should find Alice (in) and Charlie (out)
        var results = await session.Graph<Person>()
            .Where(p => p.Name == "Bob")
            .Both<Person>("knows")
            .ToListAsync();

        results.Count.ShouldBe(2);
        results.Select(n => n.Name).OrderBy(n => n).ShouldBe(["Alice", "Charlie"]);
    }

    [Test]
    public async Task Graph_Where_SetsFilter()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        var charlie = new Person { Name = "Charlie" };
        session.Store(alice);
        session.Store(bob);
        session.Store(charlie);
        await session.SaveChangesAsync();

        // Graph query with only a WHERE — no traversal steps, just filtered SELECT
        var results = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Graph_Depth_Fixed()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        // Depth 2 from Alice should reach both Bob and Charlie
        var results = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Depth(2)
            .Out<Person>("knows")
            .ToListAsync();

        results.Count.ShouldBe(2);
        results.Select(n => n.Name).OrderBy(n => n).ShouldBe(["Bob", "Charlie"]);
    }

    [Test]
    public async Task Graph_ShortestPath()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        // Get Charlie's record ID string
        var people = await session.Query<Person>().ToListAsync();
        var charlie = people.First(p => p.Name == "Charlie");
        var charlieIdStr = charlie.Id!.ToString()!;

        // Find shortest path from Alice to Charlie
        var pathResults = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .ShortestPath(charlieIdStr)
            .Out<Person>("knows")
            .ToPathListAsync();

        pathResults.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task RelateAsync_CreatesEdge()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        session.Store(alice);
        session.Store(bob);
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id;
        var bobId = people.First(p => p.Name == "Bob").Id;

        await session.RelateAsync<Knows>(aliceId!, bobId!, new Knows { Kind = "friend", Since = 2020 });

        // Verify the edge exists via graph traversal
        var results = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person>("knows")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task UnrelateAsync_RemovesEdge()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        session.Store(alice);
        session.Store(bob);
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id;
        var bobId = people.First(p => p.Name == "Bob").Id;

        await session.RelateAsync<Knows>(aliceId!, bobId!, new Knows { Kind = "friend", Since = 2020 });

        // Find the edge that was created
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(1);

        // Unrelate (delete) the edge
        await session.UnrelateAsync(edges[0].Id!, CancellationToken.None);

        // Verify the edge is gone
        var edgesAfter = await session.Query<Knows>().ToListAsync();
        edgesAfter.Count.ShouldBe(0);
    }

    [Test]
    public async Task Graph_FirstOrDefaultAsync_ReturnsSingle()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await SetupSocialGraphAsync(session);

        var result = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person>("knows")
            .FirstOrDefaultAsync();

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Graph_FirstOrDefaultAsync_ReturnsNullWhenNoMatch()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        session.Store(new Person { Name = "Alice" });
        await session.SaveChangesAsync();

        var result = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person>("knows")
            .FirstOrDefaultAsync();

        result.ShouldBeNull();
    }

    [Test]
    public async Task StoreOptions_EdgeSchema_ConfiguresCorrectTableName()
    {
        var options = new StoreOptions();
        options.Schema.Edge<Knows, Person, Person>(m => { });

        options.Schema.EdgeMappings.Count.ShouldBe(1);

        var mapping = (EdgeMapping<Knows>)options.Schema.EdgeMappings[0];
        mapping.TableName.ShouldBe("knows");
        mapping.FromTable.ShouldBe("person");
        mapping.ToTable.ShouldBe("person");
    }

    [Test]
    public async Task EdgeMapping_Index_AddsField()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "person"
        };
        mapping.Index(k => k.Since);
        mapping.Indexes.Count.ShouldBe(1);
        mapping.Indexes[0].ShouldBe("Since");
    }

    [Test]
    public async Task EdgeMapping_Index_MultipleFields()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "person"
        };
        mapping.Index(k => k.Since);
        mapping.Index(k => k.Kind);
        mapping.Indexes.Count.ShouldBe(2);
        mapping.Indexes.ShouldContain("Since");
        mapping.Indexes.ShouldContain("Kind");
    }

    [Test]
    public async Task EdgeMapping_SchemaMode_Strict()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "person"
        };
        mapping.SchemaMode.ShouldBe(SchemaMode.Flexible);

        mapping.SetSchemaMode(SchemaMode.Strict);
        mapping.SchemaMode.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public async Task EdgeMapping_SchemaMode_Flexible()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "person"
        };
        mapping.SchemaMode.ShouldBe(SchemaMode.Flexible);
    }

    [Test]
    public async Task SchemaManager_BuildDefineEdgeTable_Strict()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "project"
        };
        mapping.SetSchemaMode(SchemaMode.Strict);

        var surql = SchemaManager.BuildDefineEdgeTable(mapping);
        surql.ShouldBe("DEFINE TABLE `knows` SCHEMAFULL TYPE RELATION IN `person` OUT `project`;");
    }

    [Test]
    public async Task SchemaManager_BuildDefineEdgeTable_Flexible()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "person"
        };
        // Default is Flexible
        var surql = SchemaManager.BuildDefineEdgeTable(mapping);
        surql.ShouldBe("DEFINE TABLE `knows` SCHEMALESS TYPE RELATION IN `person` OUT `person`;");
    }

    [Test]
    public async Task SchemaManager_BuildEdgeIndexStatements()
    {
        var mapping = new EdgeMapping<Knows>
        {
            TableName = "knows",
            FromTable = "person",
            ToTable = "person"
        };
        mapping.Index(k => k.Since);
        mapping.Index(k => k.Kind);

        var statements = SchemaManager.BuildEdgeIndexStatements(mapping).ToList();
        statements.Count.ShouldBe(2);
        statements[0].ShouldBe("DEFINE INDEX idx_knows_Since ON TABLE `knows` COLUMNS Since;");
        statements[1].ShouldBe("DEFINE INDEX idx_knows_Kind ON TABLE `knows` COLUMNS Kind;");
    }
}

// ═══════════════════════════════════════════════
// PART 3: SurrealQL Generation Tests
// ═══════════════════════════════════════════════

public class GraphSurrealQLTests
{
    [Test]
    public async Task Generate_NoSteps_ProducesSelectStar()
    {
        // Plan with 0 steps, nodeType=Person
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>()
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT * FROM `person`;");
    }

    [Test]
    public async Task Generate_SingleOut_ProducesArrow()
    {
        // Out<Team>("works_in")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "works_in",
                    TargetType = typeof(Team),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->works_in->`team`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_SingleIn_ProducesBackwardArrow()
    {
        // In<Person>("child_of")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.In,
                    EdgeType = "child_of",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT <-child_of<-`person`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_MultiHop_ProducesChainedArrows()
    {
        // Out<Team>("works_in").Out<Project>("works_on")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "works_in",
                    TargetType = typeof(Team),
                    TargetKind = GraphTargetKind.Typed
                },
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "works_on",
                    TargetType = typeof(Project),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->works_in->`team`->works_on->`project`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_FixedDepth_ProducesAtBrace()
    {
        // .Depth(2).Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Depth = 2,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{2}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_RangeDepth_ProducesAtBraceRange()
    {
        // .Depth(2, 5).Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            DepthMin = 2,
            DepthMax = 5,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{2..5}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_UnboundedDepth_ProducesDoubleDot()
    {
        // .Depth().Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            IsUnboundedDepth = true,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{..}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_ShortestPath_ProducesPlusShortest()
    {
        // .ShortestPath("person:charlie").Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            IsUnboundedDepth = true,
            ShortestPathTarget = "person:charlie",
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{..+shortest=person:charlie}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_ReturnPath_ProducesPlusPath()
    {
        // .ReturnPath().Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            ReturnPath = true,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{..+path}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_CollectAll_ProducesPlusCollect()
    {
        // .CollectAll().Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            CollectAll = true,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{..+collect}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_IncludeOrigin_ProducesPlusInclusive()
    {
        // .IncludeOrigin().ShortestPath("person:charlie").Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            IsUnboundedDepth = true,
            ShortestPathTarget = "person:charlie",
            IncludeOrigin = true,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{..+shortest=person:charlie+inclusive}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_Fetch_ProducesFetchClause()
    {
        // Out<Team>("works_in").Fetch("works_in")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "works_in",
                    TargetType = typeof(Team),
                    TargetKind = GraphTargetKind.Typed
                }
            },
            FetchRelations = ["works_in"]
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->works_in->`team`.* FROM `person` FETCH works_in;");
    }

    [Test]
    public async Task Generate_Where_ProducesWhereClause()
    {
        // Plan with FilterSurql + Out<Team>("works_in")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "works_in",
                    TargetType = typeof(Team),
                    TargetKind = GraphTargetKind.Typed
                }
            },
            FilterSurql = "name = 'Alice'"
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->works_in->`team`.* FROM `person` WHERE name = 'Alice';");
    }

    [Test]
    public async Task Generate_MultiEdge_ProducesParenthesizedEdge()
    {
        // Out<Team>(new[]{"works_in", "manages"})
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeTypes = ["works_in", "manages"],
                    TargetType = typeof(Team),
                    TargetKind = GraphTargetKind.MultiEdge
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->(works_in, manages)->`team`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_Both_ProducesBidirectional()
    {
        // .Both<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Both,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT <->knows<->`person`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_IncludeIntermediate_ProducesPlusParen()
    {
        // .IncludeIntermediate().Out<Person>("child_of")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "child_of",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed,
                    IncludeIntermediate = true
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->child_of(+)->`person`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_OutAny_ProducesWildcard()
    {
        // .OutAny()
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = null,
                    TargetType = typeof(GraphNode),
                    TargetKind = GraphTargetKind.Any
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->?->`graph_node`.* FROM `person`;");
    }

    [Test]
    public async Task Generate_WhereAndFetch_Combined()
    {
        // Combined WHERE + FETCH
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "works_in",
                    TargetType = typeof(Team),
                    TargetKind = GraphTargetKind.Typed
                }
            },
            FilterSurql = "name = 'Alice'",
            FetchRelations = ["works_in"]
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->works_in->`team`.* FROM `person` WHERE name = 'Alice' FETCH works_in;");
    }

    [Test]
    public async Task Generate_DepthAndCollect_Combined()
    {
        // Depth(2) + CollectAll + Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Depth = 2,
            CollectAll = true,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{2+collect}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_DepthRangeAndReturnPath_Combined()
    {
        // Depth(1, 3) + ReturnPath + Out<Person>("knows")
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            DepthMin = 1,
            DepthMax = 3,
            ReturnPath = true,
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT @.{1..3+path}->knows->`person` FROM `person`;");
    }

    [Test]
    public async Task Generate_EmptyStepsWithDepth_SelectsStar()
    {
        // Depth(2) but no steps — should fall through to SELECT *
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            Depth = 2,
            Steps = new List<GraphStep>()
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        // With no steps, the generator outputs "SELECT * FROM table"
        sql.ShouldBe("SELECT * FROM `person`;");
    }

    [Test]
    public async Task Generate_EmptyStepsWithReturnPath_SelectsStar()
    {
        // ReturnPath but no steps — should fall through to SELECT *
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            ReturnPath = true,
            Steps = new List<GraphStep>()
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT * FROM `person`;");
    }

    [Test]
    public async Task Generate_FilterSurqlWithoutSteps()
    {
        // FilterSurql without steps — SELECT * FROM table WHERE filter
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            FilterSurql = "age > 18",
            Steps = new List<GraphStep>()
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT * FROM `person` WHERE age > 18;");
    }

    [Test]
    public async Task Generate_FilterSurqlWithSteps()
    {
        // FilterSurql with OUT step — WHERE is appended after the traversal
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Person),
            FilterSurql = "age > 18",
            Steps = new List<GraphStep>
            {
                new()
                {
                    Direction = GraphDirection.Out,
                    EdgeType = "knows",
                    TargetType = typeof(Person),
                    TargetKind = GraphTargetKind.Typed
                }
            }
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT ->knows->`person`.* FROM `person` WHERE age > 18;");
    }

    [Test]
    public async Task Generate_ProjectTableName_IsSnakeCased()
    {
        // Verify table name for Project type is snake_cased
        var plan = new GraphQueryPlan
        {
            NodeType = typeof(Project),
            Steps = new List<GraphStep>()
        };

        var sql = GraphSurrealQLGenerator.Generate(plan);
        sql.ShouldBe("SELECT * FROM `project`;");
    }
}
