namespace AeroDB;

/// <summary>Controls bulk insert behavior for document storage.</summary>
public enum BulkInsertMode
{
    /// <summary>Only insert new documents; skip existing ones.</summary>
    InsertsOnly = 0,

    /// <summary>Overwrite existing documents with the same ID.</summary>
    OverwriteExisting = 1
}
