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
            _logger.LogSendingWakeUpRequestToSlimFaas(functionName);
            using var response = await _httpClient.PostAsync(requestUri, content: null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogWakeUpForSucceededWithStatus(functionName, response.StatusCode);
            }
            else
            {
                _logger.LogWakeUpForFailedWithStatus(functionName, response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogErrorWhileCallingSlimFaasWakeUp(ex, functionName);
        }
    }
}
