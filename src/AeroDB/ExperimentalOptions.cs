namespace AeroDB;

/// <summary>
/// Groups experimental feature flags. Access via <c>o.Experimental.ML.Enabled = true</c>.
/// ML support types live in <c>AeroDB.ML</c> project.
/// </summary>
public class ExperimentalOptions
{
    /// <summary>SurrealML configuration. Types in AeroDB.ML project.</summary>
    public MlOptions ML { get; } = new();
}
