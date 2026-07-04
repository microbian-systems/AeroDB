namespace AeroDB;

/// <summary>
/// Marker interface for <see cref="IConfigureDali"/> implementations that apply
/// globally to all store types (primary and secondary). Mimics Marten's
/// <c>IGlobalConfigureMarten</c>.
/// </summary>
/// <remarks>
/// Configurators implementing only <see cref="IConfigureDali"/> are applied to
/// the primary store only. Implement <see cref="IGlobalConfigureDali"/> (in addition
/// to <see cref="IConfigureDali"/>) to have the configurator applied to secondary
/// stores as well.
/// </remarks>
public interface IGlobalConfigureDali : IConfigureDali
{
}
