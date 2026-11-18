using System.Collections.ObjectModel;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using Moq;

namespace SlimFaas.Tests;

public class DeploymentLookupTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("array")]
    [InlineData("collection")]
    public void FindFunction_PreservesFirstMatchAndOrdinalCaseSensitiveNames(string collectionType)
    {
        var first = Function("fibonacci");
        var second = Function("fibonacci");
        var functions = CreateFunctions(collectionType, [Function("FIBONACCI"), first, second]);
        var deployments = Snapshot(functions);

        Assert.Same(first, deployments.FindFunction("fibonacci"));
        Assert.Same(functions[0], deployments.FindFunction("FIBONACCI"));
        Assert.Null(deployments.FindFunction("Fibonacci"));
        Assert.Null(deployments.FindFunction("missing"));
        Assert.Null(Snapshot(CreateFunctions(collectionType, [])).FindFunction("fibonacci"));
    }

    [Fact]
    public void SearchFunction_UsesCurrentSnapshotAfterReplacement()
    {
        var original = Function("fibonacci");
        var replacement = original with { Replicas = 2 };
        var replicas = new Mock<IReplicasService>();
        replicas.SetupSequence(service => service.Deployments)
            .Returns(Snapshot([original]))
            .Returns(Snapshot([replacement]));

        Assert.Same(original, FunctionEndpointsHelpers.SearchFunction(replicas.Object, "fibonacci"));
        Assert.Same(replacement, FunctionEndpointsHelpers.SearchFunction(replicas.Object, "fibonacci"));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("array")]
    public void FindFunction_ListAndArrayQueriesAllocateNoManagedMemory(string collectionType)
    {
        var deployments = Snapshot(CreateFunctions(collectionType, [Function("first"), Function("last")]));
        for (int i = 0; i < 100; i++)
            deployments.FindFunction("last");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            deployments.FindFunction("last");
            deployments.FindFunction("missing");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static DeploymentInformation Function(string name) =>
        new(name, "default", [], new SlimFaasConfiguration(), 1);

    private static DeploymentsInformations Snapshot(IList<DeploymentInformation> functions) =>
        new(functions, new SlimFaasDeploymentInformation(1, []), []);

    private static IList<DeploymentInformation> CreateFunctions(
        string type, DeploymentInformation[] functions) => type switch
        {
            "list" => functions.ToList(),
            "array" => functions,
            "collection" => new Collection<DeploymentInformation>(functions),
            _ => throw new ArgumentException("Unknown collection type.", nameof(type))
        };
}
