using System.Net;
using System.Text;
using k8s.Autorest;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
    private static ScaleConfig NormalizeScaleConfig(ScaleConfig? config)
        => FunctionMetadataParser.NormalizeScaleConfig(config);

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
                return FunctionMetadataParser.ParseScale(
                    new Dictionary<string, string>(annotations, StringComparer.Ordinal));
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
