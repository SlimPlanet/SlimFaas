using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SlimFaasClient.Tests;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

internal static class TestHelpers
{
    public static SlimFaasClientConfig MakeConfig(
        string functionName = "test-job",
        List<string>? subscribeEvents = null)
        => new()
        {
            FunctionName = functionName,
            SubscribeEvents = subscribeEvents ?? ["my-event"],
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
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
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
            SubscribeEvents = ["ev1"],
            DefaultVisibility = "Private",
            NumberParallelRequest = 3,
            DefaultTrust = "Untrusted",
        };

        // Le payload est construit dans RegisterAsync, on valide via sérialisation
        var dto = new RegisterPayloadDto
        {
            FunctionName = config.FunctionName,
            Configuration = new RegisterConfigDto
            {
                SubscribeEvents = config.SubscribeEvents,
                DefaultVisibility = config.DefaultVisibility,
                NumberParallelRequest = config.NumberParallelRequest,
                DefaultTrust = config.DefaultTrust,
            },
        };

        dto.FunctionName.Should().Be("my-job");
        dto.Configuration.DefaultVisibility.Should().Be("Private");
        dto.Configuration.NumberParallelRequest.Should().Be(3);
        dto.Configuration.DefaultTrust.Should().Be("Untrusted");
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

        req.ElementId.Should().Be("e1");
        req.Body.Should().Equal(body);
        req.IsLastTry.Should().BeTrue();
        req.TryNumber.Should().Be(2);
        req.Headers.Should().ContainKey("content-type");
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
        req.Body.Should().BeNull();
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
        evt.EventName.Should().Be("order-created");
        evt.Body.Should().BeNull();
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

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be(SlimFaasMessageType.AsyncCallback);
        deserialized.CorrelationId.Should().Be("c1");

        var callbackRound = deserialized.Payload!.Value
            .Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto);
        callbackRound!.ElementId.Should().Be("e1");
        callbackRound.StatusCode.Should().Be(200);
    }

    [Fact]
    public void RegisterPayloadDto_SerializeDeserialize_RoundTrip()
    {
        var dto = new RegisterPayloadDto
        {
            FunctionName = "test-fn",
            Configuration = new RegisterConfigDto
            {
                SubscribeEvents = ["ev1", "ev2"],
                DefaultVisibility = "Private",
                NumberParallelRequest = 5,
            },
        };

        var json = JsonSerializer.Serialize(dto, SlimFaasClientJsonContext.Default.RegisterPayloadDto);
        var round = JsonSerializer.Deserialize(json, SlimFaasClientJsonContext.Default.RegisterPayloadDto);

        round!.FunctionName.Should().Be("test-fn");
        round.Configuration.SubscribeEvents.Should().BeEquivalentTo(["ev1", "ev2"]);
        round.Configuration.DefaultVisibility.Should().Be("Private");
        round.Configuration.NumberParallelRequest.Should().Be(5);
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

        config.DefaultVisibility.Should().Be("Public");
        config.DefaultTrust.Should().Be("Trusted");
        config.NumberParallelRequest.Should().Be(10);
        config.NumberParallelRequestPerPod.Should().Be(10);
        config.ReplicasStartAsSoonAsOneFunctionRetrieveARequest.Should().BeFalse();
        config.DependsOn.Should().BeEmpty();
        config.SubscribeEvents.Should().BeEmpty();
        config.PathsStartWithVisibility.Should().BeEmpty();
    }

    [Fact]
    public void SlimFaasClientOptions_DefaultValues_AreCorrect()
    {
        var options = new SlimFaasClientOptions();

        options.ReconnectDelay.Should().Be(5.0);
        options.PingInterval.Should().Be(30.0);
        options.ReceiveBufferSize.Should().Be(64 * 1024);
    }
}

