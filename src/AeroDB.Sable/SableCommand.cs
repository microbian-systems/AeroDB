using System.Collections.ObjectModel;

namespace AeroDB.Sable;

/// <summary>
/// Represents a parameterized SurrealQL command produced by Sable without
/// executing it.
/// </summary>
public sealed record SableCommand
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    /// <summary>
    /// Creates a command and snapshots its parameter dictionary so subsequent
    /// compiler mutations cannot change the inspected command.
    /// </summary>
    public SableCommand(
        string commandText,
        IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        ArgumentNullException.ThrowIfNull(parameters);

        CommandText = commandText;
        Parameters = parameters.Count == 0
            ? EmptyParameters
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(parameters, StringComparer.Ordinal));
    }

    /// <summary>The parameterized SurrealQL text.</summary>
    public string CommandText { get; }

    /// <summary>The named values bound when the command is executed.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Returns the parameterized command text without inlining values.</summary>
    public override string ToString() => CommandText;
}
