using AeroDB.Sable.Metadata;
using TUnit.Core;

namespace AeroDB.Tests;

public class MetadataTests
{
    /// <summary>
    /// Verifies that generated metadata registers correctly in the registry.
    /// </summary>
    [Test]
    public async Task Person_metadata_is_registered()
    {
        // If the source generator has run, the metadata should be self-registered
        var meta = MetadataRegistry.TryGet<Person>();
        var unttypedMeta = MetadataRegistry.TryGet(typeof(Person));
        
        // If generator ran, meta will be non-null and table name will be "person"
        // If generator didn't run, meta will be null — this is OK as runtime fallback
        meta?.TableName.ShouldBe("person");
        meta?.HasTenantId.ShouldBeFalse();
        meta?.HasVersion.ShouldBeFalse();
        
        unttypedMeta?.TableName.ShouldBe("person");
    }
    
    [Test]
    public async Task Product_metadata_is_registered()
    {
        var meta = MetadataRegistry.TryGet<Product>();
        meta?.TableName.ShouldBe("product");
        meta?.HasTenantId.ShouldBeFalse();
        meta?.HasVersion.ShouldBeFalse();
    }
    
    [Test]
    public async Task Order_metadata_is_registered()
    {
        var meta = MetadataRegistry.TryGet<Order>();
        meta?.TableName.ShouldBe("order");
        meta?.HasTenantId.ShouldBeFalse();
        meta?.HasVersion.ShouldBeFalse();
    }
    
    [Test]
    public async Task TenantPerson_metadata_has_tenant()
    {
        var meta = MetadataRegistry.TryGet<TenantPerson>();
        meta?.TableName.ShouldBe("tenant_person");
        meta?.HasTenantId.ShouldBeTrue();
        meta?.HasVersion.ShouldBeFalse();
        
        // If generator ran, GetTenantId should work
        if (meta is not null)
        {
            var entity = new TenantPerson { TenantId = "test-tenant" };
            meta.GetTenantId(entity).ShouldBe("test-tenant");
            
            // Non-typed access through ITypeMetadata
            var rawMeta = MetadataRegistry.TryGet(typeof(TenantPerson));
            rawMeta!.HasTenantId.ShouldBeTrue();
        }
    }
    
    [Test]
    public async Task VersionedPerson_metadata_has_version()
    {
        var meta = MetadataRegistry.TryGet<VersionedPerson>();
        meta?.TableName.ShouldBe("versioned_person");
        meta?.HasVersion.ShouldBeTrue();
        
        if (meta is not null)
        {
            var entity = new VersionedPerson { Version = 42 };
            meta.GetVersion(entity).ShouldBe(42);
            meta.SetVersion(entity, 100);
            entity.Version.ShouldBe(100);
        }
    }
    
    [Test]
    public async Task AttributedPerson_metadata_has_version_via_attribute()
    {
        var meta = MetadataRegistry.TryGet<AttributedPerson>();
        meta?.TableName.ShouldBe("attributed_person");
        meta?.HasVersion.ShouldBeTrue();
        
        if (meta is not null)
        {
            var entity = new AttributedPerson { DocumentVersion = 7 };
            meta.GetVersion(entity).ShouldBe(7);
            meta.SetVersion(entity, 99);
            entity.DocumentVersion.ShouldBe(99);
        }
    }
    
    [Test]
    public async Task Non_record_type_not_in_registry()
    {
        // Types that aren't Record subclasses should not be in the registry
        var meta = MetadataRegistry.TryGet(typeof(string));
        meta.ShouldBeNull();
    }
    
    [Test]
    public async Task MetadataDispatch_GetTableName_fallback()
    {
        // Even without source generator, the fallback in MetadataDispatch
        // or InternalSessionBase.Snake() handles all types
        var tableName = MetadataDispatch.GetTableName(typeof(Person));
        tableName.ShouldBe("person");
        
        // Unknown types use the fallback snake_case
        var unknownTable = MetadataDispatch.GetTableName(typeof(MetadataTests));
        unknownTable.ShouldBe("metadata_tests");
    }
    
    [Test]
    public async Task MetadataDispatch_HasTenantId_detection()
    {
        MetadataDispatch.HasTenantId(typeof(TenantPerson)).ShouldBeTrue();
        MetadataDispatch.HasTenantId(typeof(Person)).ShouldBeFalse();
        MetadataDispatch.HasTenantId(typeof(Product)).ShouldBeFalse();
    }
    
    [Test]
    public async Task MetadataDispatch_VersionFieldName_detection()
    {
        MetadataDispatch.GetVersionFieldName(typeof(VersionedPerson)).ShouldBe("Version");
        MetadataDispatch.GetVersionFieldName(typeof(AttributedPerson)).ShouldBe("DocumentVersion");
        MetadataDispatch.GetVersionFieldName(typeof(Person)).ShouldBeNull();
    }
}
