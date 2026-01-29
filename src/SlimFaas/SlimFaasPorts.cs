using Microsoft.Extensions.Logging;

namespace SlimFaas;

public interface ISlimFaasPorts
{
    IList<int> Ports { get; }
}

public class SlimFaasPorts : ISlimFaasPorts
{
    public IList<int> Ports { get; }

    public SlimFaasPorts(IReplicasService replicasService, ILogger<SlimFaasPorts> logger)
    {
        string slimDataUrl = (Environment.GetEnvironmentVariable(EnvironmentVariables.BaseSlimDataUrl) ??
                              EnvironmentVariables.BaseSlimDataUrlDefault);
        if(slimDataUrl.EndsWith("/"))
        {
            slimDataUrl = slimDataUrl.Substring(0, slimDataUrl.Length - 1);
        }
        int slimDataUrlPort = int.Parse(slimDataUrl.Split(":")[2]);
        var ports = replicasService.Deployments.SlimFaas.Pods.FirstOrDefault()?.Ports;

        if(ports == null)
        {
            logger.LogWarning("SlimFaas no ports found");
            Ports = new List<int>();
            return;
        }
        var mergedPorts = new List<int>(ports);
        Ports = mergedPorts.Where(p => p != slimDataUrlPort).ToList();
        foreach (int port in Ports)
        {
            logger.LogInformation("SlimFaasPorts: {Port}", port);
        }
    }
    public static string RemoveLastPathSegment(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }
        if (url.EndsWith('/'))
        {
            url = url.Substring(0, url.Length - 1);
        }

        return url;
    }
}
