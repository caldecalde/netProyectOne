using System.Reflection;
using NetArchTest.Rules;

namespace CareFlow.Architecture.Tests;

/// <summary>Enforces the Clean Architecture dependency rule in every service: dependencies point inward only.</summary>
public class LayeringTests
{
    private static readonly string[] Drivers = ["MongoDB", "Amazon", "NHapi", "Microsoft.AspNetCore"];

    public static TheoryData<string> Services => ["Ingestion", "Parser"];

    [Theory]
    [MemberData(nameof(Services))]
    public void Domain_HasNoDependencyOnOuterLayersOrFrameworks(string service)
    {
        AssertNoDependencies(Layer(service, "Domain"),
        [
            $"CareFlow.{service}.Application",
            $"CareFlow.{service}.Infrastructure",
            "CareFlow.BuildingBlocks",
            .. Drivers,
        ]);
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Application_DoesNotDependOnInfrastructureOrDrivers(string service)
    {
        AssertNoDependencies(Layer(service, "Application"), [$"CareFlow.{service}.Infrastructure", "CareFlow.BuildingBlocks.Aws", .. Drivers]);
    }

    [Theory]
    [InlineData("Ingestion", "CareFlow.Ingestion.Api")]
    [InlineData("Parser", "CareFlow.Parser.Worker")]
    public void Infrastructure_DoesNotDependOnTheHost(string service, string host)
    {
        AssertNoDependencies(Layer(service, "Infrastructure"), [host]);
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void InfrastructureAdapters_AreNotPublic(string service)
    {
        var result = Types.InAssembly(Layer(service, "Infrastructure"))
            .That().ResideInNamespaceMatching($@"CareFlow\.{service}\.Infrastructure\.(Storage|Messaging|Persistence|Hl7|HealthChecks)")
            .Should().NotBePublic()
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Services_DoNotReferenceEachOther()
    {
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            AssertNoDependencies(Layer("Ingestion", layer), ["CareFlow.Parser"]);
            AssertNoDependencies(Layer("Parser", layer), ["CareFlow.Ingestion"]);
        }
    }

    private static Assembly Layer(string service, string layer) => Assembly.Load($"CareFlow.{service}.{layer}");

    private static void AssertNoDependencies(Assembly assembly, string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {Describe(result)}");
    }

    private static string Describe(TestResult result) =>
        "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
