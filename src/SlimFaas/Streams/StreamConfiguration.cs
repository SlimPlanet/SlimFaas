using SlimData;
using SlimFaas.Kubernetes;
using System.Security.Cryptography;
using System.Text.Json;
using SlimFaas.WebSocket;

namespace SlimFaas.Streams;

internal static class StreamConfiguration
{
    public static async Task<string?> ApplyAsync(IStreamService service, DeploymentInformation function,
        HttpContext? caller, CancellationToken cancellationToken, string fingerprint = "")
    {
        var payload = await service.ReadAsync(cancellationToken);
        var subscriptions = function.SubscribeStreams ?? [];
        if (DurableStreamStore.Definitions(payload).Any(definition =>
                DurableStreamStore.Subscription(payload, definition.Name, function.Deployment) is { Active: true } active &&
                active.ConfigurationFingerprint != fingerprint))
            return "Conflicting durable function registration.";
        foreach (var subscription in subscriptions)
        {
            var existing = DurableStreamStore.Definition(payload, subscription.Name);
            if (existing is not null && (existing.Visibility != subscription.Visibility.ToString() ||
                existing.MaxAgeSeconds != function.StreamMaxAgeSeconds || existing.MaxLength != function.StreamMaxLength))
                return "Conflicting Stream configuration.";
            var active = DurableStreamStore.Subscription(payload, subscription.Name, function.Deployment);
            if (active?.Active == true && active.ConfigurationFingerprint != fingerprint)
                return "Conflicting durable function registration.";
        }
        foreach (var subscription in subscriptions)
        {
            var definition = DurableStreamStore.Definition(payload, subscription.Name) ??
                new DurableStreamDefinition { Name = subscription.Name, Visibility = subscription.Visibility.ToString() };
            if (caller is not null && !service.CanAccess(caller, definition))
                return "Stream subscription is restricted for this caller.";
            var configured = await service.MutateAsync(new()
            {
                Action = DurableStreamAction.Configure, Stream = subscription.Name,
                Visibility = subscription.Visibility.ToString(), MaxAgeSeconds = function.StreamMaxAgeSeconds,
                MaxLength = function.StreamMaxLength
            }, cancellationToken);
            if (configured.Status >= 400) return configured.Error;
            var accepted = await service.MutateAsync(new()
            {
                Action = DurableStreamAction.Subscribe, Stream = subscription.Name, Function = function.Deployment,
                FunctionVisibility = function.Visibility.ToString(), ConfigurationFingerprint = fingerprint,
                MaximumParallel = function.NumberParallelRequest, MaximumPerReplica = function.NumberParallelRequestPerPod
            }, cancellationToken);
            if (accepted.Status >= 400) return accepted.Error;
        }
        foreach (var definition in DurableStreamStore.Definitions(payload))
            if (!subscriptions.Any(subscription => subscription.Name == definition.Name) &&
                DurableStreamStore.Subscription(payload, definition.Name, function.Deployment)?.Active == true)
            {
                var result = await service.MutateAsync(new()
                {
                    Action = DurableStreamAction.Unsubscribe, Stream = definition.Name, Function = function.Deployment
                }, cancellationToken);
                if (result.Status >= 400) return result.Error;
            }
        return null;
    }

    internal static string Fingerprint(WebSocketFunctionConfiguration configuration)
    {
        var type = AppJsonContext.Default.WebSocketFunctionConfiguration;
        var normalized = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(configuration, type), type)
            ?? throw new InvalidDataException("Missing WebSocket configuration.");
        normalized.SubscribeStreams = normalized.SubscribeStreams.OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        normalized.SubscribeEvents = normalized.SubscribeEvents.OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        normalized.DependsOn = normalized.DependsOn.Order(StringComparer.Ordinal).ToList();
        normalized.PathsStartWithVisibility = normalized.PathsStartWithVisibility.OrderBy(item => item.Path, StringComparer.Ordinal).ToList();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, type)));
    }
}
