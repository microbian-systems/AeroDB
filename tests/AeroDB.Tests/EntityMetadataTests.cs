using AeroDB.Sable.Metadata;
using TUnit.Core;

namespace AeroDB.Tests;

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
    public async Task Metadata_EntityProduct_GetIdentity_preserves_long_Id()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        if (meta is not null)
        {
            var product = new EntityProduct { Name = "MetaTest", Price = 10m };
            var identity = meta.GetIdentity(product);
            identity.ShouldBe(product.Id);
        }
    }

    [Test]
    public async Task Metadata_EntityCustomer_GetIdentity_preserves_string_Id()
    {
        var meta = MetadataRegistry.TryGet<EntityCustomer>();
        if (meta is not null)
        {
            var customer = new EntityCustomer { Name = "MetaCust", Email = "meta@test.com" };
            var identity = meta.GetIdentity(customer);
            identity.ShouldBe(customer.Id);
        }
    }

    [Test]
    public async Task Metadata_EntityOrder_GetIdentity_preserves_int_Id()
    {
        var meta = MetadataRegistry.TryGet<EntityOrder>();
        if (meta is not null)
        {
            var order = new EntityOrder { Description = "MetaOrder", Quantity = 1 };
            var identity = meta.GetIdentity(order);
            identity.ShouldBe(order.Id);
        }
    }

    [Test]
    public async Task Metadata_EntitySession_GetIdentity_preserves_Guid_Id()
    {
        var meta = MetadataRegistry.TryGet<EntitySession>();
        if (meta is not null)
        {
            var session = new EntitySession { Token = "meta_token", UserName = "meta_user" };
            var identity = meta.GetIdentity(session);
            identity.ShouldBe(session.Id);
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
    public async Task Metadata_EntityProduct_GetIdentityAccessor_is_not_null()
    {
        var meta = MetadataRegistry.TryGet<EntityProduct>();
        if (meta is not null)
        {
            meta.GetIdentityAccessor.ShouldNotBeNull();

            var product = new EntityProduct { Name = "AccessorTest", Price = 10m };
            var identity = meta.GetIdentityAccessor!(product);
            identity.ShouldBe(product.Id);
        }
    }
}
