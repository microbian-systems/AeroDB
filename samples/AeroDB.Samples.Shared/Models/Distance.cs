namespace AeroDB.Samples.Shared;

public class Distance
{
    public Guid Id { get; set; }
    public double Total { get; set; }
    public int Day { get; set; }

    public override string ToString() =>
        $"{nameof(Id)}: {Id}, {nameof(Total)}: {Total}, {nameof(Day)}: {Day}";
}
