namespace SlimFaasKafka.Services;

public interface ISlimFaasClient
{
    Task WakeAsync(string functionName, CancellationToken cancellationToken = default);
}
