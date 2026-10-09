using System.Reflection;
using CareFlow.Ingestion.Application;
using CareFlow.Ingestion.Domain;
using CareFlow.Ingestion.Infrastructure;
using NetArchTest.Rules;

namespace CareFlow.Architecture.Tests;

/// <summary>Enforces the Clean Architecture dependency rule: dependencies point inward only.</summary>
public class LayeringTests
{
    private static readonly Assembly Domain = typeof(IngestionFile).Assembly;
    private static readonly Assembly Application = typeof(IngestionOptions).Assembly;
    private static readonly Assembly Infrastructure = typeof(AwsOptions).Assembly;

    [Fact]
    public void Domain_HasNoDependencyOnOuterLayersOrFrameworks()
    {
        AssertNoDependencies(Domain,
            "CareFlow.Ingestion.Application",
            "CareFlow.Ingestion.Infrastructure",
            "CareFlow.Ingestion.Api",
            "CareFlow.BuildingBlocks",
            "Microsoft.AspNetCore",
            "MongoDB",
            "Amazon");
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrDrivers()
    {
        AssertNoDependencies(Application,
            "CareFlow.Ingestion.Infrastructure",
            "CareFlow.Ingestion.Api",
            "MongoDB",
            "Amazon");
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi()
    {
        AssertNoDependencies(Infrastructure, "CareFlow.Ingestion.Api");
    }

    [Fact]
    public void InfrastructureAdapters_AreNotPublic()
    {
        var result = Types.InAssembly(Infrastructure)
            .That().ResideInNamespaceMatching(@"CareFlow\.Ingestion\.Infrastructure\.(Storage|Messaging|Persistence)")
            .Should().NotBePublic()
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static void AssertNoDependencies(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
