namespace AeroDB.Samples.Shared;

public class Movement
{
    public Direction Direction { get; set; }
    public double Distance { get; set; }

    protected bool Equals(Movement other) =>
        Direction == other.Direction && Distance.Equals(other.Distance);

    public override string ToString() =>
        $"{nameof(Direction)}: {Direction}, {nameof(Distance)}: {Distance}";

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((Movement)obj);
    }

    public override int GetHashCode() => HashCode.Combine((int)Direction, Distance);
}
