using TUnit.Core;

namespace Dali.Tests;

public class DocumentPolicyTests
{
    [Test]
    public async Task ForAllDocuments_AppliesToAllMappings()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>();
        options.Schema.For<Product>();

        var appliedTypes = new List<Type>();
        options.Policies.ForAllDocuments(m => appliedTypes.Add(m.DocumentType));

        foreach (var kvp in options.Schema.Mappings)
        {
            foreach (var policy in options.Policies.RegisteredPolicies)
                policy.Apply(kvp.Value);
        }

        appliedTypes.Count.ShouldBe(2);
        appliedTypes.ShouldContain(typeof(Person));
        appliedTypes.ShouldContain(typeof(Product));
    }

    [Test]
    public async Task ForDocumentsOfType_AppliesOnlyToMatchingType()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>();
        options.Schema.For<Product>();

        var appliedTypes = new List<Type>();
        options.Policies.ForDocumentsOfType<Person>(m => appliedTypes.Add(m.DocumentType));

        foreach (var kvp in options.Schema.Mappings)
        {
            foreach (var policy in options.Policies.RegisteredPolicies)
                policy.Apply(kvp.Value);
        }

        appliedTypes.Count.ShouldBe(1);
        appliedTypes.ShouldContain(typeof(Person));
    }

    [Test]
    public async Task AddPolicy_WithCustomPolicy_AppliesToAllMappings()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>();
        options.Schema.For<Product>();

        var appliedTypes = new List<Type>();
        options.Policies.AddPolicy(new TestPolicy(appliedTypes));
        options.Policies.ForAllDocuments(m => appliedTypes.Add(m.DocumentType));

        foreach (var kvp in options.Schema.Mappings)
        {
            foreach (var policy in options.Policies.RegisteredPolicies)
                policy.Apply(kvp.Value);
        }

        // LambdaPolicy adds once, TestPolicy adds once = 2 per mapping, 4 total
        appliedTypes.Count.ShouldBe(4);
        appliedTypes.Count(t => t == typeof(Person)).ShouldBe(2);
        appliedTypes.Count(t => t == typeof(Product)).ShouldBe(2);
    }

    [Test]
    public async Task NoPolicies_NoError()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>();

        // Should not throw when enumerating empty RegisteredPolicies
        foreach (var kvp in options.Schema.Mappings)
        {
            foreach (var policy in options.Policies.RegisteredPolicies)
                policy.Apply(kvp.Value);
        }

        options.Schema.Mappings.Count.ShouldBe(1);
    }

    [Test]
    public async Task ForAllDocuments_MultiplePolicies_AllApplied()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>();

        var applied1 = false;
        var applied2 = false;

        options.Policies.ForAllDocuments(m => applied1 = true);
        options.Policies.ForAllDocuments(m => applied2 = true);

        foreach (var kvp in options.Schema.Mappings)
        {
            foreach (var policy in options.Policies.RegisteredPolicies)
                policy.Apply(kvp.Value);
        }

        applied1.ShouldBeTrue();
        applied2.ShouldBeTrue();
    }

    [Test]
    public async Task Policies_Property_ReturnsInstance()
    {
        var options = new StoreOptions();
        var policies = options.Policies;
        policies.ShouldNotBeNull();
        policies.ShouldBeOfType<DocumentPolicies>();
    }

    private sealed class TestPolicy : IDocumentPolicy
    {
        private readonly List<Type> _appliedTypes;
        public TestPolicy(List<Type> appliedTypes) => _appliedTypes = appliedTypes;

        public void Apply(DocumentMapping mapping)
        {
            _appliedTypes.Add(mapping.DocumentType);
        }
    }
}
