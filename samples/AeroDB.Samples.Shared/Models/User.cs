namespace AeroDB.Samples.Shared;

public class User
{
    public User() { Id = Guid.NewGuid(); }
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Nickname { get; set; }
    public bool Internal { get; set; }
    public string Department { get; set; } = "";
    public int Age { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public List<Friend> Friends { get; set; } = new();
    public string[] Roles { get; set; } = Array.Empty<string>();
}

public class Friend
{
    public string Name { get; set; } = "";
}
