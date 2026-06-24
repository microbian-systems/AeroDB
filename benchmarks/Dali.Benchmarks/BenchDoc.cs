using SurrealDb.Net.Models;

namespace Dali.Benchmarks;

public class BenchDoc : Record
{
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = new();
    public InnerData? Nested { get; set; }

    public static BenchDoc[] Generate(int count)
    {
        var faker = new Bogus.Faker<BenchDoc>()
            .RuleFor(d => d.Name, f => f.Person.FullName)
            .RuleFor(d => d.Number, f => f.Random.Int(1, 10000))
            .RuleFor(d => d.CreatedAt, f => f.Date.Past())
            .RuleFor(d => d.Tags, f => f.Make(f.Random.Int(1, 5), () => f.Lorem.Word()).ToList())
            .RuleFor(d => d.Nested, f => new InnerData { Value = f.Lorem.Sentence(), Count = f.Random.Int(1, 100) });
        return faker.Generate(count).ToArray();
    }
}

public class InnerData
{
    public string Value { get; set; } = "";
    public int Count { get; set; }
}
