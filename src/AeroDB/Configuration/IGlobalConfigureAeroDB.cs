namespace AeroDB;

/// <summary>
/// Marker interface for <see cref="IConfigureAeroDB"/> implementations that apply
/// globally to all store types (primary and secondary). Mimics Marten's
/// <c>IGlobalConfigureMarten</c>.
/// </summary>
/// <remarks>
/// Configurators implementing only <see cref="IConfigureAeroDB"/> are applied to
/// the primary store only. Implement <see cref="IGlobalConfigureAeroDB"/> (in addition
/// to <see cref="IConfigureAeroDB"/>) to have the configurator applied to secondary
/// stores as well.
/// </remarks>
public interface IGlobalConfigureAeroDB : IConfigureAeroDB
{
}
