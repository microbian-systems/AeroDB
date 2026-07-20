namespace AeroDB.Sable;

/// <summary>Base exception for explicit SurrealDB hashing operations.</summary>
public class SableHashingException : Exception
{
    public SableHashingException(string message)
        : base(message)
    {
    }

    public SableHashingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A hashing algorithm or session is not valid for the requested operation.</summary>
public sealed class SableHashingConfigurationException : SableHashingException
{
    public SableHashingConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>The connected SurrealDB server could not complete a hashing operation.</summary>
public class SableHashingOperationException : SableHashingException
{
    internal SableHashingOperationException(
        SurrealHashAlgorithm algorithm,
        string operation,
        Exception innerException)
        : base(
            $"SurrealDB hashing operation '{operation}' is unavailable for algorithm '{algorithm}'.",
            innerException)
    {
    }
}

/// <summary>The connected server does not recognize a catalogued crypto function.</summary>
public sealed class SableHashFunctionUnavailableException : SableHashingOperationException
{
    internal SableHashFunctionUnavailableException(
        SurrealHashAlgorithm algorithm,
        string operation)
        : base(
            algorithm,
            operation,
            new NotSupportedException("The connected server does not expose this function."))
    {
    }
}
