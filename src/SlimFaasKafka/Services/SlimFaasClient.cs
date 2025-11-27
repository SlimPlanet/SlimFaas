using Microsoft.Extensions.Options;
using SlimFaasKafka.Config;

namespace SlimFaasKafka.Services;

public sealed class SlimFaasClient : ISlimFaasClient
{
    private readonly HttpClient _httpClient;
    private readonly SlimFaasOptions _options;
    private readonly ILogger<SlimFaasClient> _logger;

    public SlimFaasClient(
        HttpClient httpClient,
        IOptions<SlimFaasOptions> options,
        ILogger<SlimFaasClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task WakeAsync(string functionName, CancellationToken cancellationToken = default)
    {
        var path = _options.WakeUpPathTemplate.Replace(
            "{functionName}",
            functionName,
            StringComparison.OrdinalIgnoreCase);

        var requestUri = new Uri(path, UriKind.Relative);

        try
        {
            _logger.LogInformation("Sending wake up request to SlimFaas for function {Function}", functionName);
            using var response = await _httpClient.PostAsync(requestUri, content: null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Wake up for {Function} succeeded with status code {StatusCode}",
                    functionName,
                    response.StatusCode);
            }
            else
            {
                _logger.LogWarning(
                    "Wake up for {Function} failed with status code {StatusCode}",
                    functionName,
                    response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Error while calling SlimFaas wake up for function {Function}", functionName);
        }
    }
}
