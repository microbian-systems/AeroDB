namespace AeroDB;

/// <summary>
/// Controls which metadata columns are enabled on stored events.
/// Configured via <c>StoreOptions.Events.MetadataConfig</c>.
/// </summary>
public class MetadataConfig
{
    /// <summary>Enable CorrelationId tracking on events.</summary>
    public bool CorrelationIdEnabled { get; private set; }

    /// <summary>Enable CausationId tracking on events.</summary>
    public bool CausationIdEnabled { get; private set; }

    /// <summary>Enable Headers dictionary on events. Default is true.</summary>
    public bool HeadersEnabled { get; private set; } = true;

    /// <summary>Enable CorrelationId column on events.</summary>
    public MetadataConfig EnableCorrelationId()
    {
        CorrelationIdEnabled = true;
        return this;
    }

    /// <summary>Enable CausationId column on events.</summary>
    public MetadataConfig EnableCausationId()
    {
        CausationIdEnabled = true;
        return this;
    }

    /// <summary>Enable Headers column on events.</summary>
    public MetadataConfig EnableHeaders()
    {
        HeadersEnabled = true;
        return this;
    }

    /// <summary>Enable all metadata columns.</summary>
    public MetadataConfig EnableAll()
    {
        CorrelationIdEnabled = true;
        CausationIdEnabled = true;
        HeadersEnabled = true;
        return this;
    }
}
