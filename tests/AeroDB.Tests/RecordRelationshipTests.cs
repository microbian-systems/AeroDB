using System.Linq.Expressions;
using AeroDB.Sable;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class RecordRelationshipTests
{
    [Test]
    public async Task NamingPolicy_Formats_BuiltIn_Cases()
    {
        new AeroDbCaseNamingPolicy(AeroDbNameCase.SnakeCaseLower)
            .FieldName(nameof(RelationshipOrder.CreatedOn))
            .ShouldBe("created_on");

        new AeroDbCaseNamingPolicy(AeroDbNameCase.CamelCase)
            .FieldName(nameof(RelationshipOrder.CreatedOn))
            .ShouldBe("createdOn");

        new AeroDbCaseNamingPolicy(AeroDbNameCase.PascalCase)
            .FieldName(nameof(RelationshipOrder.CreatedOn))
            .ShouldBe("CreatedOn");
    }

    [Test]
    public async Task HasOne_Builds_RecordLink_Field_Ddl()
    {
        var options = SnakeCaseOptions();
        var mapping = options.Schema.For<RelationshipOrder>();
        mapping.HasOne(x => x.Customer);

        var relationship = mapping.GetRelationshipMappings().Single();
        var ddl = SchemaManager.BuildRelationshipFieldStatement(relationship);

        ddl.ShouldBe("DEFINE FIELD customer ON TABLE relationship_order TYPE record<relationship_customer>;");
    }

    [Test]
    public async Task HasMany_Builds_RecordLinkArray_Field_Ddl()
    {
        var options = SnakeCaseOptions();
        var mapping = options.Schema.For<RelationshipOrder>();
        mapping.HasMany(x => x.Products);

        var relationship = mapping.GetRelationshipMappings().Single();
        var ddl = SchemaManager.BuildRelationshipFieldStatement(relationship);

        ddl.ShouldBe("DEFINE FIELD products ON TABLE relationship_order TYPE array<record<relationship_product>>;");
    }

    [Test]
    public async Task ResolveRelationships_Registers_ScalarFk_Convention_Mapping()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<ConventionFixtures.Customer>().Identity(x => x.Id);
        options.Schema.For<ConventionFixtures.Order>().Identity(x => x.Id);

        var relationship = options.Schema.FindRelationship(typeof(ConventionFixtures.Order), typeof(ConventionFixtures.Customer), nameof(ConventionFixtures.Order.CustomerId));

        relationship.ShouldNotBeNull();
        relationship.StorageKind.ShouldBe(RelationshipStorageKind.ScalarForeignKey);
        relationship.Cardinality.ShouldBe(RelationshipCardinality.One);
        relationship.SourceFieldName.ShouldBe("customer_id");
        relationship.TargetIdMemberName.ShouldBe(nameof(ConventionFixtures.Customer.Id));
        relationship.Origin.ShouldBe(RelationshipOrigin.Convention);
    }

    [Test]
    public async Task ResolveRelationships_Uses_SourceGenerated_ScalarFk_Candidate_When_Available()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<GeneratedCustomer>().Identity(x => x.Id);
        options.Schema.For<GeneratedOrder>().Identity(x => x.Id);

        var relationship = options.Schema.FindRelationship(typeof(GeneratedOrder), typeof(GeneratedCustomer), nameof(GeneratedOrder.GeneratedCustomerId));

        relationship.ShouldNotBeNull();
        relationship.StorageKind.ShouldBe(RelationshipStorageKind.ScalarForeignKey);
        relationship.Origin.ShouldBe(RelationshipOrigin.SourceGenerated);
    }

    [Test]
    public async Task ScalarFk_Relationship_Does_Not_Emit_RecordLink_Ddl()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<ConventionFixtures.Customer>().Identity(x => x.Id);
        options.Schema.For<ConventionFixtures.Order>().Identity(x => x.Id);

        options.Schema.ResolveRelationships();
        var mapping = options.Schema.Mappings[typeof(ConventionFixtures.Order)];
        var relationship = mapping.GetRelationshipMappings()
            .Single(r => r.ClrMemberName == nameof(ConventionFixtures.Order.CustomerId));

        Should.Throw<InvalidOperationException>(() => SchemaManager.BuildRelationshipFieldStatement(relationship));
    }

    [Test]
    public async Task ResolveRelationships_Downgrades_TypeMismatch_To_Scalar_Field()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<PocoCustomer>().Identity(x => x.Id);
        options.Schema.For<MismatchedCustomerOrder>().Identity(x => x.Id);

        var relationship = options.Schema.FindRelationship(
            typeof(MismatchedCustomerOrder),
            typeof(PocoCustomer),
            nameof(MismatchedCustomerOrder.CustomerId));

        relationship.ShouldBeNull();
    }

    [Test]
    public async Task ResolveRelationships_Throws_On_Duplicate_Target_Type_Names()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<DuplicateCustomerOrder>().Identity(x => x.Id);
        options.Schema.For<DuplicateModuleA.Customer>().Identity(x => x.Id);
        options.Schema.For<DuplicateModuleB.Customer>().Identity(x => x.Id);

        var ex = Should.Throw<InvalidOperationException>(() => options.Schema.ResolveRelationships());
        ex.Message.ShouldContain(typeof(DuplicateModuleA.Customer).FullName!);
        ex.Message.ShouldContain(typeof(DuplicateModuleB.Customer).FullName!);
    }

    [Test]
    public async Task HasOne_ScalarFk_Validates_Against_Target_Identity()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<PocoCustomer>().Identity(x => x.Id);

        var mapping = options.Schema.For<PocoOrder>();
        mapping.HasOne<PocoCustomer>(x => x.CustomerId);

        var relationship = mapping.GetRelationshipMappings().Single(r => r.ClrMemberName == nameof(PocoOrder.CustomerId));
        relationship.StorageKind.ShouldBe(RelationshipStorageKind.ScalarForeignKey);
        relationship.StorageFieldName.ShouldBe("customer_id");
    }

    [Test]
    public async Task Relationship_Projection_Uses_NamingPolicy_Dot_Path()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Select(x => new { x.Customer!.Name });

        query.ToCommand().ShouldBe("SELECT customer.name FROM `relationship_order`;");
    }

    [Test]
    public async Task Relationship_Where_Uses_NamingPolicy_Dot_Path()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Where(x => x.Customer!.Name == "Alice");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE customer.name = $p0;");
    }

    [Test]
    public async Task Relationship_Where_Can_Filter_Source_And_RecordLink_Target()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);
        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Where(o => o.CreatedOn >= orderedOnOrAfter && o.Customer!.Name == "Alice");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE (created_on >= d'2026-01-01T00:00:00Z') AND (customer.name = $p0);");
    }

    [Test]
    public async Task WhereTarget_Shorthand_Delegates_To_Link_For_RecordLink()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Where((RelationshipOrder o, RelationshipCustomer c) => c.Name == "Alice");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE customer.name = $p0;");
    }

    [Test]
    public async Task LinkedWhere_Can_Filter_Source_And_RecordLink_Target()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);
        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Link<RelationshipCustomer>(o => o.Customer)
            .Where((o, c) => o.CreatedOn >= orderedOnOrAfter && c.Name == "Alice");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE (created_on >= d'2026-01-01T00:00:00Z') AND (customer.name = $p0);");
    }

    [Test]
    public async Task JoinedWhere_Can_Infer_Target_Type_From_Join()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Join<RelationshipCustomer>(o => o.Customer)
            .Where((o, c) => c.Name == "Alice");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE customer.name = $p0;");
    }

    [Test]
    public async Task LinkedWhere_TypedFk_Uses_Computed_RecordId_DotTraversal()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipEntityOrder>();

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipEntityOrder>(provider)
            .Link<RelationshipCustomer>(o => o.CustomerId)
            .Where((o, c) => c.Name == "Alice");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_entity_order` WHERE type::record(\"relationship_customer\", customer_id).name = $p0;");
    }

    [Test]
    public async Task LinkedWhere_TypedFk_Coalesces_Multiple_Target_Conditions()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipEntityOrder>();

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipEntityOrder>(provider)
            .Link<RelationshipCustomer>(o => o.CustomerId)
            .Where((o, c) => c.Name == "Alice" && c.Age >= 30);

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_entity_order` WHERE type::record(\"relationship_customer\", customer_id).name = $p0 AND type::record(\"relationship_customer\", customer_id).age >= $p1;");
    }

    [Test]
    public async Task Relationship_Ddl_Supports_Reference_OnDelete_And_Unique()
    {
        var options = SnakeCaseOptions();
        var mapping = options.Schema.For<RelationshipOrder>();
        mapping.HasOne(x => x.Customer).OnDeleteCascade().Unique();

        var relationship = mapping.GetRelationshipMappings().Single();

        SchemaManager.BuildRelationshipFieldStatement(relationship)
            .ShouldBe("DEFINE FIELD customer ON TABLE relationship_order TYPE record<relationship_customer> REFERENCE ON DELETE CASCADE;");

        SchemaManager.BuildRelationshipIndexStatement(relationship)
            .ShouldBe("DEFINE INDEX uidx_relationship_order_customer ON TABLE relationship_order COLUMNS customer UNIQUE;");
    }

    [Test]
    public async Task MultiLink_Where_Composes_RecordLink_And_TypedFk()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Link<RelationshipCustomer>(o => o.Customer)
            .Link<RelationshipProduct>(o => o.ProductId)
            .Where((o, c, p) => c.Name == "Alice" && p.Name == "Widget");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE (customer.name = $p0) AND (type::record(\"relationship_product\", product_id).name = $p1);");
    }

    [Test]
    public async Task MultiLink_Where_Supports_Three_RecordLink_Targets()
    {
        var options = SnakeCaseOptions();
        var mapping = options.Schema.For<RelationshipOrder>();
        mapping.HasOne(x => x.Customer);
        mapping.HasOne(x => x.Product);
        mapping.HasOne(x => x.SalesRep);
        var orderedOnOrAfter = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<RelationshipOrder>(provider)
            .Link<RelationshipCustomer>(o => o.Customer)
            .Link<RelationshipProduct>(o => o.Product)
            .Link<RelationshipSalesRep>(o => o.SalesRep)
            .Where((o, c, p, s) => o.CreatedOn >= orderedOnOrAfter
                && c.Name == "Alice"
                && p.Name == "Widget"
                && s.Name == "Troy");

        query.ToCommand().ShouldBe("SELECT * FROM `relationship_order` WHERE (((created_on >= d'2026-01-01T00:00:00Z') AND (customer.name = $p0)) AND (product.name = $p1)) AND (sales_rep.name = $p2);");
    }

    [Test]
    public async Task RelationshipMutations_Build_ArrayAdd_And_Remove_Statements()
    {
        var options = SnakeCaseOptions();
        var mapping = options.Schema.For<RelationshipOrder>();
        mapping.HasOne(x => x.Customer);
        mapping.HasMany(x => x.Products);

        RelationshipMutationBuilder<RelationshipOrder>.BuildAddStatement(
                options,
                123,
                x => x.Products,
                456)
            .ShouldBe("UPDATE relationship_order:123 SET products = array::add(products, relationship_product:456);");

        RelationshipMutationBuilder<RelationshipOrder>.BuildRemoveStatement(
                options,
                123,
                x => x.Products,
                456)
            .ShouldBe("UPDATE relationship_order:123 SET products -= relationship_product:456;");

        RelationshipMutationBuilder<RelationshipOrder>.BuildSetStatement(
                options,
                123,
                x => x.Customer,
                789)
            .ShouldBe("UPDATE relationship_order:123 SET customer = relationship_customer:789;");

        RelationshipMutationBuilder<RelationshipOrder>.BuildUnsetStatement(
                options,
                123,
                x => x.Customer)
            .ShouldBe("UPDATE relationship_order:123 SET customer = NONE;");
    }

    [Test]
    public async Task IncludeReverse_Convention_Uses_Child_HasOne_FieldName()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<ReverseParent>();
        options.Schema.For<ReverseChild>()
            .HasOne(x => x.Parent)
            .FieldName("parent_order");

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var concrete = new SurrealDbQueryable<ReverseParent>(provider);
        ISurrealDbQueryable<ReverseParent> queryable = concrete;

        SurrealDbQueryableExtensions.IncludeReverse<ReverseParent, ReverseChild>(queryable, x => x.Items);

        var spec = concrete.IncludeSpecs.Single();
        spec.ForeignKeyClrName.ShouldBe(nameof(ReverseChild.Parent));
        spec.ForeignKeyField.ShouldBe("parent_order");
    }

    [Test]
    public async Task ThenInclude_RecordLinks_Adds_Nested_Fetch_Path()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<ThenOrder>().HasOne(x => x.Customer);
        options.Schema.For<ThenCustomer>().HasOne(x => x.Address);
        options.Schema.For<ThenAddress>();

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<ThenOrder>(provider)
            .Include(x => x.Customer)
            .ThenInclude(x => x.Address);

        query.ToCommand().ShouldBe("SELECT * FROM `then_order` FETCH `customer`.`address`;");
    }

    [Test]
    public async Task CompiledQuery_LinkWhere_Uses_Resolved_Relationship_Metadata()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<RelationshipOrder>().HasOne(x => x.Customer);
        options.Schema.For<RelationshipCustomer>();

        var plan = CompiledQueryPlanner.GetOrBuildPlan<
            RelationshipOrder,
            IEnumerable<RelationshipOrder>>(
            new CompiledRelationshipOrdersByCustomerName { Name = "Alice" },
            options);

        plan.SkeletonResult.ToSurrealQL()
            .ShouldBe("SELECT * FROM `relationship_order` WHERE customer.name = $p0;");
    }

    [Test]
    public async Task CompiledQuery_ThenInclude_Uses_Resolved_Relationship_Metadata()
    {
        var options = SnakeCaseOptions();
        options.Schema.For<ThenOrder>().HasOne(x => x.Customer);
        options.Schema.For<ThenCustomer>().HasOne(x => x.Address);
        options.Schema.For<ThenAddress>();

        var plan = CompiledQueryPlanner.GetOrBuildPlan<
            ThenOrder,
            IEnumerable<ThenOrder>>(
            new CompiledThenIncludeOrders(),
            options);

        plan.SkeletonResult.ToSurrealQL()
            .ShouldBe("SELECT * FROM `then_order` FETCH `customer`.`address`;");
    }

    [Test]
    public async Task PocoLinkedWhere_TypedFk_Uses_Computed_RecordId_DotTraversal()
    {
        var options = SnakeCaseOptions();
        ConfigurePocoRelationshipSchema(options);
        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<PocoOrder>(provider)
            .Link<PocoCustomer>(o => o.CustomerId)
            .Where((o, c) => o.CreatedOn >= orderedOnOrAfter && c.Name == "Alice" && c.Age >= 30);

        query.ToCommand().ShouldBe("SELECT * FROM `poco_order` WHERE ((created_on >= d'2026-01-01T00:00:00Z') AND (type::record(\"poco_customer\", <string>customer_id).name = $p0)) AND (type::record(\"poco_customer\", <string>customer_id).age >= $p1);");
    }

    [Test]
    public async Task PocoMultiLink_Where_Composes_Three_TypedFk_Targets()
    {
        var options = SnakeCaseOptions();
        ConfigurePocoRelationshipSchema(options);
        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var provider = new SurrealQueryProvider(Substitute.For<ISurrealDbSession>(), options);
        var query = new SurrealDbQueryable<PocoOrder>(provider)
            .Link<PocoCustomer>(o => o.CustomerId)
            .Link<PocoProduct>(o => o.ProductId)
            .Join<PocoSalesRep>(o => o.SalesRepId)
            .Where((o, c, p, s) => o.CreatedOn >= orderedOnOrAfter
                && c.Name == "Alice"
                && p.Name == "Widget"
                && s.Name == "Troy");

        query.ToCommand().ShouldBe("SELECT * FROM `poco_order` WHERE (((created_on >= d'2026-01-01T00:00:00Z') AND (type::record(\"poco_customer\", <string>customer_id).name = $p0)) AND (type::record(\"poco_product\", <string>product_id).name = $p1)) AND (type::record(\"poco_sales_rep\", <string>sales_rep_id).name = $p2);");
    }

    [Test]
    [NotInParallel]
    public async Task LinkedWhere_Executes_Against_InMemory_RecordLink()
    {
        await using var store = await CreateRelationshipStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedRelationshipRecordsAsync(session);

        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var query = session.Query<RelationshipOrder>()
            .Link<RelationshipCustomer>(o => o.Customer)
            .Where((o, c) => o.CreatedOn >= orderedOnOrAfter && c.Name == "Alice");

        var count = await query.CountAsync();

        count.ShouldBe(1);
    }

    [Test]
    [NotInParallel]
    public async Task RelationshipMutations_Add_And_Remove_Execute_Against_InMemory_RecordLink_Array()
    {
        await using var store = await CreateRelationshipStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedRelationshipRecordsAsync(session, includeProducts: false);

        await session.Relationships<RelationshipOrder>()
            .For(100)
            .Add(x => x.Products, 10);

        await session.Relationships<RelationshipOrder>()
            .For(100)
            .Add(x => x.Products, 10);

        var withProduct = await session.RawQueryAsync<dynamic>(
            "SELECT * FROM relationship_order WHERE array::len(Products) = 1 AND Products[0].Name = 'Widget';");
        withProduct.Count.ShouldBe(1);

        await session.Relationships<RelationshipOrder>()
            .For(100)
            .Remove(x => x.Products, 10);

        var withoutProducts = await session.RawQueryAsync<dynamic>(
            "SELECT * FROM relationship_order WHERE array::len(Products) = 0;");
        withoutProducts.Count.ShouldBe(1);
    }

    [Test]
    [NotInParallel]
    public async Task RelationshipMutations_Set_And_Unset_Execute_Against_InMemory_RecordLink()
    {
        await using var store = await CreateRelationshipStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedRelationshipRecordsAsync(session, includeCustomer: false);

        await session.Relationships<RelationshipOrder>()
            .For(100)
            .Set(x => x.Customer, 1);

        var linkedToAlice = await session.Query<RelationshipOrder>()
            .Link<RelationshipCustomer>(o => o.Customer)
            .Where((o, c) => c.Name == "Alice")
            .CountAsync();
        linkedToAlice.ShouldBe(1);

        await session.Relationships<RelationshipOrder>()
            .For(100)
            .Unset(x => x.Customer);

        var unlinked = await session.RawQueryAsync<dynamic>(
            "SELECT * FROM relationship_order WHERE Customer = NONE;");
        unlinked.Count.ShouldBe(1);
    }

    [Test]
    [NotInParallel]
    public async Task PocoLinkedWhere_Executes_Against_InMemory_TypedFk()
    {
        await using var store = await CreatePocoRelationshipStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPocoRelationshipRecordsAsync(session);

        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var query = session.Query<PocoOrder>()
            .Link<PocoCustomer>(o => o.CustomerId)
            .Where((o, c) => o.CreatedOn >= orderedOnOrAfter && c.Name == "Alice" && c.Age >= 30);

        var count = await query.CountAsync();

        count.ShouldBe(1);
    }

    [Test]
    [NotInParallel]
    public async Task PocoMultiLink_Executes_Against_InMemory_TypedFk_Targets()
    {
        await using var store = await CreatePocoRelationshipStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPocoRelationshipRecordsAsync(session);

        var orderedOnOrAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var count = await session.Query<PocoOrder>()
            .Link<PocoCustomer>(o => o.CustomerId)
            .Link<PocoProduct>(o => o.ProductId)
            .Join<PocoSalesRep>(o => o.SalesRepId)
            .Where((o, c, p, s) => o.CreatedOn >= orderedOnOrAfter
                && c.Name == "Alice"
                && p.Name == "Widget"
                && s.Name == "Troy")
            .CountAsync();

        count.ShouldBe(1);
    }

    private static StoreOptions SnakeCaseOptions()
    {
        var options = new StoreOptions();
        options.Schema.Case = AeroDbNameCase.SnakeCaseLower;
        return options;
    }

    private static void ConfigurePocoRelationshipSchema(StoreOptions options)
    {
        options.Schema.For<PocoCustomer>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        options.Schema.For<PocoProduct>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        options.Schema.For<PocoSalesRep>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        options.Schema.For<PocoOrder>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
    }

    private static Task<IDocumentStore> CreateRelationshipStoreAsync()
    {
        return TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.Case = AeroDbNameCase.PascalCase;
            options.Schema.For<RelationshipCustomer>();
            options.Schema.For<RelationshipProduct>();
            options.Schema.For<RelationshipSalesRep>();

            var order = options.Schema.For<RelationshipOrder>();
            order.HasOne(x => x.Customer).Optional();
            order.HasOne(x => x.Product);
            order.HasOne(x => x.SalesRep);
            order.HasMany(x => x.Products);
        });
    }

    private static Task<IDocumentStore> CreatePocoRelationshipStoreAsync()
    {
        return TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.Case = AeroDbNameCase.PascalCase;
            ConfigurePocoRelationshipSchema(options);
        });
    }

    private static async Task SeedRelationshipRecordsAsync(
        IDocumentSession session,
        bool includeCustomer = true,
        bool includeProducts = true)
    {
        var alice = new RelationshipCustomer
        {
            Id = new RecordIdOf<int>("relationship_customer", 1),
            Name = "Alice",
            Age = 31
        };
        var bob = new RelationshipCustomer
        {
            Id = new RecordIdOf<int>("relationship_customer", 2),
            Name = "Bob",
            Age = 42
        };
        var widget = new RelationshipProduct
        {
            Id = new RecordIdOf<int>("relationship_product", 10),
            Name = "Widget"
        };
        var salesRep = new RelationshipSalesRep
        {
            Id = new RecordIdOf<int>("relationship_sales_rep", 20),
            Name = "Troy"
        };

        session.Store(alice);
        session.Store(bob);
        session.Store(widget);
        session.Store(salesRep);
        session.Store(new RelationshipOrder
        {
            Id = new RecordIdOf<int>("relationship_order", 100),
            CreatedOn = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            Customer = includeCustomer ? alice : null,
            Product = widget,
            SalesRep = salesRep,
            ProductId = 10,
            Products = includeProducts ? [widget] : []
        });

        await session.SaveChangesAsync();
    }

    private static async Task SeedPocoRelationshipRecordsAsync(IDocumentSession session)
    {
        session.Store(new PocoCustomer { Id = 1, Name = "Alice", Age = 31 });
        session.Store(new PocoCustomer { Id = 2, Name = "Bob", Age = 42 });
        session.Store(new PocoProduct { Id = 10, Name = "Widget" });
        session.Store(new PocoProduct { Id = 11, Name = "Gadget" });
        session.Store(new PocoSalesRep { Id = 20, Name = "Troy" });
        session.Store(new PocoOrder
        {
            Id = 100,
            CreatedOn = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            CustomerId = 1,
            ProductId = 10,
            SalesRepId = 20
        });
        session.Store(new PocoOrder
        {
            Id = 101,
            CreatedOn = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            CustomerId = 2,
            ProductId = 11,
            SalesRepId = 20
        });

        await session.SaveChangesAsync();
    }
}

public sealed class RelationshipOrder : Record
{
    public DateTimeOffset CreatedOn { get; set; }
    public RelationshipCustomer? Customer { get; set; }
    public RelationshipProduct? Product { get; set; }
    public RelationshipSalesRep? SalesRep { get; set; }
    public long ProductId { get; set; }
    public List<RelationshipProduct> Products { get; set; } = [];
}

public sealed class RelationshipCustomer : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public sealed class RelationshipProduct : Record
{
    public string Name { get; set; } = "";
}

public sealed class RelationshipSalesRep : Record
{
    public string Name { get; set; } = "";
}

public sealed class RelationshipEntityOrder : Entity<long>
{
    public long CustomerId { get; set; }
}

public sealed class PocoOrder
{
    public long Id { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public long CustomerId { get; set; }
    public long ProductId { get; set; }
    public long SalesRepId { get; set; }
}

public sealed class PocoCustomer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public sealed class PocoProduct
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class PocoSalesRep
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public static class ConventionFixtures
{
    public sealed class Order
    {
        public long Id { get; set; }
        public long CustomerId { get; set; }
    }

    public sealed class Customer
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
    }
}

public sealed class GeneratedOrder : Entity<long>
{
    public long GeneratedCustomerId { get; set; }
}

public sealed class GeneratedCustomer : Entity<long>
{
    public string Name { get; set; } = "";
}

public sealed class MismatchedCustomerOrder
{
    public long Id { get; set; }
    public string CustomerId { get; set; } = "";
}

public sealed class DuplicateCustomerOrder
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
}

public static class DuplicateModuleA
{
    public sealed class Customer
    {
        public long Id { get; set; }
    }
}

public static class DuplicateModuleB
{
    public sealed class Customer
    {
        public long Id { get; set; }
    }
}

public sealed class ReverseParent : Record
{
    public IEnumerable<ReverseChild>? Items { get; set; } = [];
}

public sealed class ReverseChild : Record
{
    public ReverseParent? Parent { get; set; }
}

public sealed class ThenOrder : Record
{
    public ThenCustomer? Customer { get; set; }
}

public sealed class ThenCustomer : Record
{
    public ThenAddress? Address { get; set; }
}

public sealed class ThenAddress : Record
{
    public string City { get; set; } = "";
}

public sealed class CompiledRelationshipOrdersByCustomerName : ICompiledListQuery<RelationshipOrder>
{
    public string Name { get; set; } = "";

    public Expression<Func<ISurrealDbQueryable<RelationshipOrder>, IEnumerable<RelationshipOrder>>> QueryIs()
        => q => q
            .Link<RelationshipCustomer>(x => x.Customer)
            .Where((o, c) => c.Name == Name);
}

public sealed class CompiledThenIncludeOrders : ICompiledListQuery<ThenOrder>
{
    public Expression<Func<ISurrealDbQueryable<ThenOrder>, IEnumerable<ThenOrder>>> QueryIs()
        => q => q
            .Include(x => x.Customer)
            .ThenInclude(x => x.Address);
}
