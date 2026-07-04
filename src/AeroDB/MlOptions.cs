namespace AeroDB;

/// <summary>
/// Experimental ML options. Feature-gated via <c>o.Experimental.ML.Enabled = true</c>.
/// When enabled, probes SurrealDB for ML availability during store initialization.
/// Full ML API lives in <c>AeroDB.ML</c> project.
/// </summary>
public class MlOptions
{
    /// <summary>Whether ML inference is expected to be available.</summary>
    public bool Enabled { get; set; }
}
