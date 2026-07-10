using AeroDB.Sable;

namespace AeroDB.Tests;

/// <summary>
/// Parent type for testing IncludeReverse with Entity types.
/// Has a <see cref="Children"/> collection loaded via reverse include.
/// </summary>
public class EntityIncludeParent : EntitySnowlake
{
    public string Title { get; set; } = "";
    public List<EntityIncludeChild> Children { get; set; } = new();
}

/// <summary>
/// Child type for testing IncludeReverse with Entity types.
/// Has a <see cref="ParentId"/> FK pointing back to <see cref="EntityIncludeParent"/>.
/// </summary>
public class EntityIncludeChild : EntitySnowlake
{
    public string Content { get; set; } = "";
    public long ParentId { get; set; }
}

// String FK variant
public class EntityIncludeParentStr : EntityString
{
    public string Title { get; set; } = "";
    public List<EntityIncludeChildStr> Items { get; set; } = new();
}

public class EntityIncludeChildStr : EntityString
{
    public string Name { get; set; } = "";
    public string ParentId { get; set; } = "";  // FK: string pointing to parent's string Id
}

// Int FK variant
public class EntityIncludeParentInt : EntityInt
{
    public string Title { get; set; } = "";
    public List<EntityIncludeChildInt> Items { get; set; } = new();
}

public class EntityIncludeChildInt : EntityInt
{
    public string Name { get; set; } = "";
    public int ParentId { get; set; }  // FK: int pointing to parent's int Id
}

// Guid FK variant
public class EntityIncludeParentGuid : EntityGuid
{
    public string Title { get; set; } = "";
    public List<EntityIncludeChildGuid> Items { get; set; } = new();
}

public class EntityIncludeChildGuid : EntityGuid
{
    public string Name { get; set; } = "";
    public Guid ParentId { get; set; }  // FK: Guid pointing to parent's Guid Id
}
