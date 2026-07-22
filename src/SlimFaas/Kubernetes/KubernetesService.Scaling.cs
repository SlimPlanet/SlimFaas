using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s.Autorest;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
    private static ScaleConfig NormalizeScaleConfig(ScaleConfig? cfg)
    {
        // objet racine
        cfg ??= new ScaleConfig();

        // Behavior (et ses sous-objets)
        var behavior = cfg.Behavior ?? new ScaleBehavior();

        var scaleUp = behavior.ScaleUp ?? ScaleDirectionBehavior.DefaultScaleUp();
        var scaleDown = behavior.ScaleDown ?? ScaleDirectionBehavior.DefaultScaleDown();

        // Policies (si null -> defaults)
        var upPolicies = scaleUp.Policies is { Count: > 0 } ? scaleUp.Policies : ScaleDirectionBehavior.DefaultScaleUp().Policies;
        var downPolicies = scaleDown.Policies is { Count: > 0 } ? scaleDown.Policies : ScaleDirectionBehavior.DefaultScaleDown().Policies;

        // Triggers (si null -> liste vide)
        var triggers = cfg.Triggers ?? new List<ScaleTrigger>();

        // On reconstruit via 'with' (records immutables)
        return cfg with
        {
            Triggers = triggers,
            Behavior = behavior with
            {
                ScaleUp = scaleUp with { Policies = upPolicies },
                ScaleDown = scaleDown with { Policies = downPolicies }
            }
        };
    }

    private static ScaleConfig? GetScaleConfig(
        IDictionary<string, string> annotations,
        string name,
        ILogger<KubernetesService> logger)
    {
        try
        {
            if (annotations.TryGetValue(Scale, out string? annotation) &&
                !string.IsNullOrWhiteSpace(annotation))
            {
                annotation = JsonMinifier.MinifyJson(annotation);
                if (!string.IsNullOrEmpty(annotation))
                {
                    // AOT-safe : on n'utilise QUE le contexte source-généré
                    var opts = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        TypeInfoResolver = ScaleConfigSerializerContext.Default
                    };
                    // Enums en string (insensible à la casse) — AOT OK
                    opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

                    var parsed = JsonSerializer.Deserialize<ScaleConfig>(annotation, opts);

                    // IMPORTANT : on normalise pour injecter les defaults si des sous-objets manquent
                    return NormalizeScaleConfig(parsed);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "name: {Name}\n annotations[Scale]: {Annotation}", name, annotations.TryGetValue(Scale, out var a) ? a : "<missing>");
        }

        return null;
    }

    public async Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
    {
        try
        {
            k8s.Kubernetes client = _client;
            string patchString = $"{{\"spec\": {{\"replicas\": {request.Replicas}}}}}";
            // we need to get the base uri, as it's not set on the HttpClient
            switch (request.PodType)
            {
                case PodType.Deployment:
                    {
                        string url = string.Concat(client.BaseUri,
                            $"apis/apps/v1/namespaces/{request.Namespace}/deployments/{request.Deployment}/scale");
                        using HttpRequestMessage httpRequest = new(HttpMethod.Patch, new Uri(url))
                        {
                            Content = new StringContent(
                                patchString,
                                Encoding.UTF8,
                                "application/merge-patch+json")
                        };
                        if (client.Credentials != null)
                        {
                            await client.Credentials.ProcessHttpRequestAsync(httpRequest, CancellationToken.None);
                        }

                        using HttpResponseMessage response =
                            await client.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                        if (response.StatusCode != HttpStatusCode.OK)
                        {
                            throw new HttpOperationException("Error while scaling deployment");
                        }

                        break;
                    }
                case PodType.StatefulSet:
                    {
                        string url = string.Concat(client.BaseUri,
                            $"apis/apps/v1/namespaces/{request.Namespace}/statefulsets/{request.Deployment}/scale");
                        using HttpRequestMessage httpRequest = new(HttpMethod.Patch, new Uri(url))
                        {
                            Content = new StringContent(
                                patchString,
                                Encoding.UTF8,
                                "application/merge-patch+json")
                        };
                        if (client.Credentials != null)
                        {
                            await client.Credentials.ProcessHttpRequestAsync(httpRequest, CancellationToken.None);
                        }

                        using HttpResponseMessage response =
                            await client.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                        if (response.StatusCode != HttpStatusCode.OK)
                        {
                            throw new HttpOperationException("Error while scaling deployment");
                        }

                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(request.PodType.ToString());
            }
        }
        catch (HttpOperationException e)
        {
            _logger.LogError(e, "Error while scaling kubernetes deployment {RequestDeployment}", request.Deployment);
            return request;
        }

        return request;
    }
}
