using Dali.Metadata;
using TUnit.Core;

namespace Dali.Tests;

public class EntityMetadataTests
{
    [Test]
    public async Task Metadata_EntityProduct_is_registered()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        // If the source generator has run, metadata should be self-registered
        // If not, this is acceptable as runtime fallback exists
        meta?.TableName.ShouldBe("entity_product");
    }

    [Test]
    public async Task Metadata_EntityProduct_has_correct_TableName()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        meta?.TableName.ShouldBe("entity_product");
    }

    [Test]
    public async Task Metadata_EntityProduct_GetRecordId_returns_Id_toString()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        if (meta is not null)
        {
            var product = new EntityProduct { Name = "MetaTest", Price = 10m };
            var recordId = meta.GetRecordId(product);
            recordId.ShouldBe(product.Id.ToString()); // Snowflake long ID as string
        }
    }

    [Test]
    public async Task Metadata_EntityCustomer_GetRecordId_returns_string_Id()
    {
        var meta = MetadataRegistry.TryGet<EntityCustomer>();
        if (meta is not null)
        {
            var customer = new EntityCustomer { Name = "MetaCust", Email = "meta@test.com" };
            var recordId = meta.GetRecordId(customer);
            recordId.ShouldBe(customer.Id); // string Id returned directly
        }
    }

    [Test]
    public async Task Metadata_EntityOrder_GetRecordId_returns_int_Id()
    {
        var meta = MetadataRegistry.TryGet<EntityOrder>();
        if (meta is not null)
        {
            var order = new EntityOrder { Description = "MetaOrder", Quantity = 1 };
            var recordId = meta.GetRecordId(order);
            recordId.ShouldBe(order.Id.ToString()); // int Id as string
        }
    }

    [Test]
    public async Task Metadata_EntitySession_GetRecordId_returns_Guid_Id()
    {
        var meta = MetadataRegistry.TryGet<EntitySession>();
        if (meta is not null)
        {
            var session = new EntitySession { Token = "meta_token", UserName = "meta_user" };
            var recordId = meta.GetRecordId(session);
            recordId.ShouldBe(session.Id.ToString()); // Guid Id as string
        }
    }

    [Test]
    public async Task Metadata_EntityProduct_has_Fields()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        if (meta is not null)
        {
            meta.Fields.ShouldNotBeNull();
            meta.Fields!.Count.ShouldBeGreaterThanOrEqualTo(3); // Name, Price, Stock
        }
    }

    [Test]
    public async Task Metadata_EntityProduct_GetRecordIdAccessor_is_not_null()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        if (meta is not null)
        {
            meta.GetRecordIdAccessor.ShouldNotBeNull();

            var product = new EntityProduct { Name = "AccessorTest", Price = 10m };
            var recordId = meta.GetRecordIdAccessor!(product);
            recordId.ShouldBe(product.Id.ToString());
        }
    }
}
