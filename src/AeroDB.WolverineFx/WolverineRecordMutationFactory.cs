using AeroDB.Sable;
using SurrealDb.Net.Models;

namespace AeroDB.WolverineFx;

/// <summary>
/// Converts the message store's established bound envelope values into finite,
/// typed Sable mutations. SQL text and named parameters are deliberately not
/// considered here.
/// </summary>
internal static class WolverineRecordMutationFactory
{
    public static ValidatedRecordMutation CreateIncoming(EnvelopeSqlCommand command) =>
        Create(command.Values, WolverineTableNames.Incoming);

    public static ValidatedRecordMutation CreateOutgoing(EnvelopeSqlCommand command) =>
        Create(command.Values, WolverineTableNames.Outgoing);

    public static ValidatedRecordMutation MarkIncomingHandled(EnvelopeSqlCommand command)
    {
        var values = command.Values;
        if (values.Length != 2 || values[1] is not DateTimeOffset keepUntil)
            throw new InvalidOperationException("The handled-envelope mutation requires exactly a canonical id and keep-until timestamp.");

        var target = Target(WolverineTableNames.Incoming, values[0]);
        return ValidatedRecordMutation.MergeExisting(target, new Dictionary<string, object?>
        {
            ["status"] = "Handled",
            ["keep_until"] = keepUntil
        });
    }

    private static ValidatedRecordMutation Create(object[] values, string table)
    {
        if (values.Length != 2 || values[1] is not IReadOnlyDictionary<string, object?> content)
            throw new InvalidOperationException("The envelope create mutation requires exactly a canonical id and content dictionary.");

        return ValidatedRecordMutation.Create(Target(table, values[0]), content);
    }

    private static RecordId Target(string table, object? value)
    {
        if (value is not string id
            || !Guid.TryParseExact(id, "D", out var parsed)
            || !string.Equals(id, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The envelope mutation requires a canonical GUID string identifier.");
        }

        return RecordId.From(table, id);
    }
}
