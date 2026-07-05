using System.Globalization;

namespace AeroDB;

internal static class GeometryExtensions
{
    public static string ToInvariantString(this double value)
        => value.ToString(CultureInfo.InvariantCulture);
}
