using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IWakeUpFunction
{
    Task FireAndForgetWakeUpAsync(string functionName);
}

public class WakeUpFunction(IServiceScopeFactory serviceScopeFactory, ILogger<WakeUpFunction> logger) : IWakeUpFunction
{
    readonly List<string> _runningFunctions = new();
    readonly object _lock = new();
    private static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
    {
        DeploymentInformation? function =
            replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
        return function;
    }

    public async Task FireAndForgetWakeUpAsync(string functionName)
    {
        lock (_lock)
        {
            if(_runningFunctions.Contains(functionName))
                return;
            _runningFunctions.Add(functionName);
        }
        await Task.Run(async () =>
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var serviceProvider = scope.ServiceProvider;
                var replicasService = serviceProvider.GetRequiredService<IReplicasService>();
                var historyHttpService = serviceProvider.GetRequiredService<HistoryHttpMemoryService>();
                DeploymentInformation? function = SearchFunction(replicasService, functionName);
                if (function != null)
                {
                    historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
                    await Task.Delay(1000);
                    function = SearchFunction(replicasService, functionName);
                    if (function == null)
                    {
                        return;
                    }
                    var numberPods = function.Pods.Count(p => p.Ready.HasValue && p.Ready.Value);
                    while (numberPods == 0)
                    {
                        historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
                        function = SearchFunction(replicasService, functionName);
                        if (function != null)
                        {
                            numberPods = function.Pods.Count(p => p.Ready.HasValue && p.Ready.Value);
                        }
                        await Task.Delay(1000);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error in wake up function");
                throw;
            }
            finally
            {
                lock (_lock)
                {
                    _runningFunctions.Remove(functionName);
                }
            }
        });
    }
}
