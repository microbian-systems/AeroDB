namespace AeroDB.Samples.Shared;

public enum Colors { Red, Blue, Green, Purple, Yellow, Orange }

public class Target
{
    public Guid Id { get; set; }
    public string String { get; set; } = "";
    public string AnotherString { get; set; } = "";
    public int Number { get; set; }
    public int AnotherNumber { get; set; }
    public Guid OtherGuid { get; set; }
    public bool Flag { get; set; }
    public double Float { get; set; }
    public long Long { get; set; }
    public double Double { get; set; }
    public DateTime Date { get; set; }
    public DateTimeOffset DateOffset { get; set; }
    public Colors Color { get; set; }
    public int[] NumberArray { get; set; } = Array.Empty<int>();
    public string[] StringArray { get; set; } = Array.Empty<string>();
    public string? PaddedString { get; set; }
}
