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
    public TenancyStyle TenancyStyle { get; set; }
    public string? DefaultTenantId { get; set; }
    public EventSourcingOptions Events { get; } = new();

    public Func<ISurrealDbClient>? ClientFactory { get; set; }

    /// <summary>
    /// Registered projections (inline and async).
    /// </summary>
    public List<IProjection> Projections { get; } = new();

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

public enum TenancyStyle
{
    None,
    Conjoined,
    DatabasePerTenant
}

public class EventSourcingOptions
{
    public bool Enabled { get; set; }
}
