namespace SlimFaas;

public interface ISlimFaasPorts
{
    IList<int> Ports { get; }
}

public class SlimFaasPorts : ISlimFaasPorts
{
    public IList<int> Ports { get; }

    public SlimFaasPorts(IReplicasService replicasService)
    {
        int slimDataUrlPort = int.Parse((Environment.GetEnvironmentVariable(EnvironmentVariables.BaseSlimDataUrl) ??
                                        EnvironmentVariables.BaseSlimDataUrlDefault).Split(":")[2]);
        var slimFaasLitensAdditionalPorts = EnvironmentVariables.ReadIntegers(EnvironmentVariables.SlimFaasListenAdditionalPorts,
        EnvironmentVariables.SlimFaasListenAdditionalPortsDefault);
        var ports = replicasService.Deployments.SlimFaas.Pods.FirstOrDefault()?.Ports;

        if(ports == null)
        {
            Console.WriteLine($"Slimfaas no ports found");
            Ports = new List<int>();
            return;
        }
        var mergedPorts = new List<int>(ports);
        mergedPorts.AddRange(slimFaasLitensAdditionalPorts);
        Ports = mergedPorts.Where(p => p != slimDataUrlPort).ToList();
    }
}
