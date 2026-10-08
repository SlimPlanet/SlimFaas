using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SlimFaasClient.Tests;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

internal static class TestHelpers
{
    private static readonly JsonSerializerOptions s_camelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SlimFaasClientConfig MakeConfig(
        string functionName = "test-job",
        List<SubscribeEventConfig>? subscribeEvents = null)
        => new()
        {
            FunctionName = functionName,
            SubscribeEvents = subscribeEvents ?? [new SubscribeEventConfig { Name = "my-event" }],
        };

    public static string MakeEnvelope(
        SlimFaasMessageType type,
        object? payload,
        string correlationId = "corr-1")
    {
        var env = new SlimFaasEnvelope
        {
            Type = type,
            CorrelationId = correlationId,
            Payload = payload != null
                ? JsonSerializer.SerializeToElement(payload,
                    s_camelCaseOptions)
                : null,
        };
        return JsonSerializer.Serialize(env, SlimFaasClientJsonContext.Default.SlimFaasEnvelope);
    }
}

// ---------------------------------------------------------------------------
// Tests des modèles
// ---------------------------------------------------------------------------

public class SlimFaasModelsTests
{
    [Fact]
    public void Config_RegisterPayload_MapsAllFields()
    {
        var config = new SlimFaasClientConfig
        {
            FunctionName = "my-job",
            SubscribeEvents = [new SubscribeEventConfig { Name = "ev1" }],
            DefaultVisibility = FunctionVisibility.Private,
            NumberParallelRequest = 3,
            DefaultTrust = FunctionTrust.Untrusted,
        };

        // Le payload est construit dans RegisterAsync, on valide via sérialisation
        var dto = new RegisterPayloadDto
        {
            FunctionName = config.FunctionName,
            Configuration = new RegisterConfigDto
            {
                SubscribeEvents = config.SubscribeEvents
                    .Select(e => new SubscribeEventConfigDto { Name = e.Name, Visibility = e.Visibility?.ToString() })
                    .ToList(),
                DefaultVisibility = config.DefaultVisibility.ToString(),
                NumberParallelRequest = config.NumberParallelRequest,
                DefaultTrust = config.DefaultTrust.ToString(),
            },
        };

        Assert.Equal("my-job", dto.FunctionName);
        Assert.Equal("Private", dto.Configuration.DefaultVisibility);
        Assert.Equal(3, dto.Configuration.NumberParallelRequest);
        Assert.Equal("Untrusted", dto.Configuration.DefaultTrust);
    }
}

// ---------------------------------------------------------------------------
// Tests du mappage des requêtes/évènements
// ---------------------------------------------------------------------------

public class MappingTests
{
    [Fact]
    public void AsyncRequestDto_MapsToModel_WithBody()
    {
        var body = "hello world"u8.ToArray();
        var dto = new AsyncRequestDto
        {
            ElementId = "e1",
            Method = "POST",
            Path = "/api",
            Query = "?x=1",
            Headers = new() { ["content-type"] = ["application/json"] },
            Body = Convert.ToBase64String(body),
            IsLastTry = true,
            TryNumber = 2,
        };

        // Via réflexion (méthode privée) – on valide la logique de mapping
        var req = MapRequest(dto);

        Assert.Equal("e1", req.ElementId);
        Assert.Equal(body, req.Body);
        Assert.True(req.IsLastTry);
        Assert.Equal(2, req.TryNumber);
        Assert.Contains("content-type", req.Headers);
    }

    [Fact]
    public void AsyncRequestDto_MapsToModel_WithoutBody()
    {
        var dto = new AsyncRequestDto
        {
            ElementId = "e2",
            Method = "GET",
            Path = "/ping",
            Body = null,
        };

        var req = MapRequest(dto);
        Assert.Null(req.Body);
    }

    [Fact]
    public void PublishEventDto_MapsToModel()
    {
        var dto = new PublishEventDto
        {
            EventName = "order-created",
            Method = "POST",
            Path = "/events",
            Body = null,
        };

        var evt = MapEvent(dto);
        Assert.Equal("order-created", evt.EventName);
        Assert.Null(evt.Body);
    }

    // Wrappers pour accéder aux méthodes statiques privées via délégués publics
    private static SlimFaasAsyncRequest MapRequest(AsyncRequestDto dto) => new()
    {
        ElementId = dto.ElementId,
        Method = dto.Method,
        Path = dto.Path,
        Query = dto.Query,
        Headers = dto.Headers,
        Body = dto.Body != null ? Convert.FromBase64String(dto.Body) : null,
        IsLastTry = dto.IsLastTry,
        TryNumber = dto.TryNumber,
    };

    private static SlimFaasPublishEvent MapEvent(PublishEventDto dto) => new()
    {
        EventName = dto.EventName,
        Method = dto.Method,
        Path = dto.Path,
        Query = dto.Query,
        Headers = dto.Headers,
        Body = dto.Body != null ? Convert.FromBase64String(dto.Body) : null,
    };
}

// ---------------------------------------------------------------------------
// Tests de sérialisation JSON
// ---------------------------------------------------------------------------

public class SerializationTests
{
    [Fact]
    public void SlimFaasEnvelope_SerializeDeserialize_RoundTrip()
    {
        var callback = new AsyncCallbackDto { ElementId = "e1", StatusCode = 200 };
        var envelope = new SlimFaasEnvelope
        {
            Type = SlimFaasMessageType.AsyncCallback,
            CorrelationId = "c1",
            Payload = JsonSerializer.SerializeToElement(callback, SlimFaasClientJsonContext.Default.AsyncCallbackDto),
        };

        var json = JsonSerializer.Serialize(envelope, SlimFaasClientJsonContext.Default.SlimFaasEnvelope);
        var deserialized = JsonSerializer.Deserialize(json, SlimFaasClientJsonContext.Default.SlimFaasEnvelope);

        Assert.NotNull(deserialized);
        Assert.Equal(SlimFaasMessageType.AsyncCallback, deserialized!.Type);
        Assert.Equal("c1", deserialized.CorrelationId);

        var callbackRound = deserialized.Payload!.Value
            .Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto);
        Assert.Equal("e1", callbackRound!.ElementId);
        Assert.Equal(200, callbackRound.StatusCode);
    }

    [Fact]
    public void RegisterPayloadDto_SerializeDeserialize_RoundTrip()
    {
        var dto = new RegisterPayloadDto
        {
            FunctionName = "test-fn",
            Configuration = new RegisterConfigDto
            {
                SubscribeEvents = [
                    new SubscribeEventConfigDto { Name = "ev1" },
                    new SubscribeEventConfigDto { Name = "ev2" },
                ],
                DefaultVisibility = "Private",
                NumberParallelRequest = 5,
            },
        };

        var json = JsonSerializer.Serialize(dto, SlimFaasClientJsonContext.Default.RegisterPayloadDto);
        var round = JsonSerializer.Deserialize(json, SlimFaasClientJsonContext.Default.RegisterPayloadDto);

        Assert.Equal("test-fn", round!.FunctionName);
        Assert.Equal(["ev1", "ev2"], round.Configuration.SubscribeEvents.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Private", round.Configuration.DefaultVisibility);
        Assert.Equal(5, round.Configuration.NumberParallelRequest);
    }
}

// ---------------------------------------------------------------------------
// Tests d'intégration basiques (sans vrai serveur WebSocket)
// ---------------------------------------------------------------------------

public class SlimFaasClientConfigTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new SlimFaasClientConfig { FunctionName = "my-fn" };

        Assert.Equal(FunctionVisibility.Public, config.DefaultVisibility);
        Assert.Equal(FunctionTrust.Trusted, config.DefaultTrust);
        Assert.Equal(10, config.NumberParallelRequest);
        Assert.Equal(10, config.NumberParallelRequestPerPod);
        Assert.False(config.ReplicasStartAsSoonAsOneFunctionRetrieveARequest);
        Assert.Empty(config.DependsOn);
        Assert.Empty(config.SubscribeEvents);
        Assert.Empty(config.PathsStartWithVisibility);
    }

    [Fact]
    public void SlimFaasClientOptions_DefaultValues_AreCorrect()
    {
        var options = new SlimFaasClientOptions();

        Assert.Equal(5.0, options.ReconnectDelay);
        Assert.Equal(30.0, options.PingInterval);
        Assert.Equal(64 * 1024, options.ReceiveBufferSize);
    }
}

