namespace AeroDB.Sable.Configuration;

/// <summary>Base exception for embedded Sable configuration-store failures.</summary>
public class SableConfigurationException : Exception
{
    public SableConfigurationException(string message)
        : base(message)
    {
    }

    public SableConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An attempted operation conflicts with the read-only .NET configuration snapshot.
/// </summary>
public sealed class SableConfigurationReadOnlyException : SableConfigurationException
{
    public SableConfigurationReadOnlyException()
        : base(
            "The Sable IConfiguration provider is read-only. Persist changes with " +
            "ISableConfigurationStore.SetAsync(...) or DeleteAsync(...), then refresh the provider.")
    {
    }
}

/// <summary>The persistent store contains duplicate case-insensitive keys.</summary>
public sealed class SableConfigurationDuplicateKeyException : SableConfigurationException
{
    public SableConfigurationDuplicateKeyException(string key)
        : base($"The Sable configuration store contains a duplicate logical key '{key}'.")
    {
    }
}
