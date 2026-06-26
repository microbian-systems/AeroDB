using TUnit.Core;

namespace Dali.Tests;

public class EntitySchemaTests
{
    [Test]
    public async Task Schema_For_EntityType_creates_mapping()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<EntityProduct>();
        mapping.ShouldNotBeNull();
        mapping.EntityType.ShouldBe(typeof(EntityProduct));
    }

    [Test]
    public async Task Schema_For_EntityType_Supports_SetSchemaMode()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<EntityProduct>().SetSchemaMode(SchemaMode.Flexible);
        mapping.SchemaModeType.ShouldBe(SchemaMode.Flexible);
    }

    [Test]
    public async Task Schema_For_EntityType_Supports_Index()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<EntityProduct>().Index(p => p.Name);
        mapping.Indices.Count.ShouldBe(1);
        mapping.Indices[0].Columns.ShouldContain("Name");
    }

    [Test]
    public async Task Schema_For_EntityType_Supports_UniqueIndex()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<EntityProduct>().UniqueIndex(p => p.Name);
        mapping.Indices.Count.ShouldBe(1);
        mapping.Indices[0].IsUnique.ShouldBeTrue();
    }

    [Test]
    public async Task Schema_For_EntityType_is_idempotent()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var m1 = options.Schema.For<EntityProduct>();
        var m2 = options.Schema.For<EntityProduct>();
        m1.ShouldBeSameAs(m2);
    }

    [Test]
    public async Task Schema_For_mixed_Record_and_Entity_types()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var recordMapping = options.Schema.For<Person>().Index(p => p.Name);
        var entityMapping = options.Schema.For<EntityProduct>().Index(p => p.Name);

        recordMapping.ShouldNotBeNull();
        entityMapping.ShouldNotBeNull();
        recordMapping.Indices.Count.ShouldBe(1);
        entityMapping.Indices.Count.ShouldBe(1);
    }

    [Test]
    public async Task Schema_For_string_throws_ArgumentException()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        Should.Throw<ArgumentException>(() => options.Schema.For<string>());
    }

    [Test]
    public async Task Schema_For_int_throws_ArgumentException()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        Should.Throw<ArgumentException>(() => options.Schema.For<int>());
    }

    [Test]
    public async Task Schema_For_abstract_class_throws_ArgumentException()
    {
        // Entity<TId> is abstract — should be rejected
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        Should.Throw<ArgumentException>(() => options.Schema.For<Entity<long>>());
    }

    [Test]
    public async Task Schema_For_Record_type_does_not_throw()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        // Record types like Person should NOT throw
        var mapping = options.Schema.For<Person>();
        mapping.ShouldNotBeNull();
    }

    [Test]
    public async Task Schema_For_Entity_type_does_not_throw()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        // Entity types like EntityProduct should NOT throw
        var mapping = options.Schema.For<EntityProduct>();
        mapping.ShouldNotBeNull();
    }
}
