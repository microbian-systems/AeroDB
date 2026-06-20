using SurrealDb.Net;

namespace Dali;

public class StoreOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";
    public string? Namespace { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }

    public SchemaOptions Schema { get; } = new();
    public MultiTenancyOptions MultiTenancy { get; } = new();
    public EventSourcingOptions Events { get; } = new();

    public Func<ISurrealDbClient>? ClientFactory { get; set; }

    public StoreOptions Connection(string endpoint, string? ns = null, string? db = null,
        string? username = null, string? password = null, string? token = null)
    {
        Endpoint = endpoint;
        Namespace = ns;
        Database = db;
        Username = username;
        Password = password;
        Token = token;
        return this;
    }
}

public class SchemaOptions
{
    public bool AutoCreate { get; set; } = true;
}

public class MultiTenancyOptions
{
    public bool Enabled { get; set; }
}

public class EventSourcingOptions
{
    public bool Enabled { get; set; }
}
