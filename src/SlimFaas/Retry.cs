namespace SlimFaas;

public static class Retry
{

    public static async Task<T> DoAsync<T>(
        Func<Task<T>> action,
        ILogger logger,
        IList<int> delays
    )
    {
        var exceptions = new List<Exception>();

        for (int attempt = -1; attempt < delays.Count; attempt++)
        {
            try
            {
                if (attempt >= 0)
                {
                    var delay = delays[attempt];
                    logger.LogTryWaitNumberSecond(attempt, delay);
                    await Task.Delay(delay * 1000);
                }

                return await action();
            }
            catch (Exception ex)
            {
                if (IsNonRetryable(ex))
                    throw;

                logger.LogSlimDataServiceDoAsync(ex);
                exceptions.Add(ex);
            }
        }

        if (exceptions.LastOrDefault() is SlimData.SlimDataUnavailableException unavailable)
            throw unavailable;

        throw new AggregateException(exceptions);
    }

    public static async Task DoAsync(
        Func<Task> action,
        ILogger logger,
        IList<int> delays
    )
    {
        var exceptions = new List<Exception>();

        for (int attempt = -1; attempt < delays.Count; attempt++)
        {
            try
            {
                if (attempt >= 0)
                {
                    var delay = delays[attempt];
                    logger.LogTryWaitNumberSecond2(attempt, delay);
                    await Task.Delay(delay * 1000);
                }
                await action();
                return;
            }
            catch (Exception ex)
            {
                if (IsNonRetryable(ex))
                    throw;

                logger.LogSlimDataServiceDoAsync2(ex);
                exceptions.Add(ex);
            }
        }

        if (exceptions.LastOrDefault() is SlimData.SlimDataUnavailableException unavailable)
            throw unavailable;

        throw new AggregateException(exceptions);
    }

    public static async Task<HttpResponseMessage> DoRequestAsync(
        Func<Task<HttpResponseMessage>> action,
        ILogger logger,
        IList<int> delays,
        IList<int> httpStatusRetries
    )
    {
        var exceptions = new List<Exception>();

        for (int attempt = -1; attempt < delays.Count; attempt++)
        {
            if (attempt >= 0)
            {
                var delay = delays[attempt];
                logger.LogDoRequestAsyncTryWaitNumberSecond(attempt + 1, delay);
                await Task.Delay(delay * 1000);
            }

            var responseMessage = await WrapRequestAction(action, logger);
            var statusCode = (int)responseMessage.StatusCode;
            if (!httpStatusRetries.Contains(statusCode))
            {
                return responseMessage;
            }
            responseMessage.Dispose();
            exceptions.Add(new HttpRequestException($"DoRequestAsync received code Http {statusCode}", null, responseMessage.StatusCode));
        }

        throw new AggregateException(exceptions);
    }

    private static async Task<HttpResponseMessage> WrapRequestAction(Func<Task<HttpResponseMessage>> action, ILogger logger)
    {
        try
        {
            return await action();
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            var fallbackResponse = new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("Error 504 simulated due to a timeout")
            };
            return fallbackResponse;
        }
        catch (HttpRequestException ex)
        {
            logger.LogNetworkException(ex);

            var fallbackResponse = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Error 500 simulated due to a network problem")
            };
            return fallbackResponse;
        }
    }

    private static bool IsNonRetryable(Exception exception)
        => exception is SlimData.BatchQueueFullException or SlimData.BatchItemTooLargeException;

}
