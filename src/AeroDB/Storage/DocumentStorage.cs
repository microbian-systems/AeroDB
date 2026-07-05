using AeroDB.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB;

public class DocumentStorage
{
    public static string GetTableName<T>()
        => MetadataDispatch.GetTableName(typeof(T));

    public static RecordIdOf<TId> CreateRecordId<TId>(string table, TId id)
        where TId : notnull
        => new(table, id);

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
