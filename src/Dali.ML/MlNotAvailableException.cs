namespace Dali;

/// <summary>
/// Thrown when SurrealML functions (<c>ml::</c>) are not available on the connected SurrealDB server.
/// This typically means the server was not compiled with the <c>--features ml</c> flag,
/// or the <c>ml</c> function family is blocked by server capability rules.
/// </summary>
public class MlNotAvailableException : InvalidOperationException
{
    public MlNotAvailableException(string message) : base(message) { }
    public MlNotAvailableException(string message, Exception inner) : base(message, inner) { }
}
