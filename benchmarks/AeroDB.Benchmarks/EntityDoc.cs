using AeroDB;

namespace AeroDB.Benchmarks;

public class EntityDoc : Entity<long>
{
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = new();
    public InnerData? Nested { get; set; }

    public static EntityDoc[] Generate(int count)
    {
        var faker = new Bogus.Faker<EntityDoc>()
            .RuleFor(d => d.Id, _ => SnowflakeGenerator.NewId())
            .RuleFor(d => d.Name, f => f.Person.FullName)
            .RuleFor(d => d.Number, f => f.Random.Int(1, 10000))
            .RuleFor(d => d.CreatedAt, f => f.Date.Past())
            .RuleFor(d => d.Tags, f => f.Make(f.Random.Int(1, 5), () => f.Lorem.Word()).ToList())
            .RuleFor(d => d.Nested, f => new InnerData { Value = f.Lorem.Sentence(), Count = f.Random.Int(1, 100) });
        return faker.Generate(count).ToArray();
    }
}

// InnerData is defined in BenchDoc.cs — DO NOT redefine it here.
