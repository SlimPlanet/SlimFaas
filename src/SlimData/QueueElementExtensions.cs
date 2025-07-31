using System.Collections.Immutable;

namespace SlimData;

public static class QueueElementExtensions
{
    public static bool IsTimeout(this QueueElement element, long nowTicks)
    {
        if (element.RetryQueueElements.Count <= 0) return false;
        int timeout = element.HttpTimeout;
        var retryQueueElement = element.RetryQueueElements[^1];
        if (retryQueueElement.EndTimeStamp == 0 &&
            retryQueueElement.StartTimeStamp + TimeSpan.FromSeconds(timeout).Ticks <= nowTicks)
        {
            return true;
        }
        return false;   
    }
    
    public static bool IsWaitingForRetry(this QueueElement element, long nowTicks)
    {
        ImmutableList<int> retries = element.TimeoutRetries;
        var count = element.RetryQueueElements.Count;
        if (count == 0 || count > retries.Count) return false;
        
        if (element.IsFinished(nowTicks)) return false;
        if (element.IsRunning(nowTicks)) return false;
        
        var retryQueueElement = element.RetryQueueElements[^1];
        var retryTimeout = retries[count - 1];
        if (element.IsTimeout(nowTicks) && TimeSpan.FromSeconds(retryTimeout).Ticks + element.HttpTimeout > nowTicks - retryQueueElement.StartTimeStamp)
        {
            return true;
        }

        if (retryQueueElement.EndTimeStamp != 0 &&
            (TimeSpan.FromSeconds(retryTimeout).Ticks > nowTicks - retryQueueElement.EndTimeStamp))
        {
            return true;
        }
        
        return false;
    }
    
    public static bool IsFinished(this QueueElement queueElement, long nowTicks)
    {
        var count = queueElement.RetryQueueElements.Count;
        if (count <= 0) return false;
        ImmutableList<int> retries = queueElement.TimeoutRetries;
        var retryQueueElement = queueElement.RetryQueueElements[^1];
        if (retryQueueElement.EndTimeStamp > 0 &&
            !queueElement.HttpStatusRetries.Contains(retryQueueElement.HttpCode))
        {
            return true;
        }

        if (retries.Count < count)
        {
            if (queueElement.IsTimeout(nowTicks) || retryQueueElement.EndTimeStamp > 0)
            {
                return true;
            }
        }
        
        return false;
    }
    
    public static bool IsRunning(this QueueElement queueElement, long nowTicks)
    {
        if (queueElement.RetryQueueElements.Count <= 0) return false;
        var retryQueueElement = queueElement.RetryQueueElements[^1];
        if (retryQueueElement.EndTimeStamp == 0 &&
            !queueElement.IsTimeout(nowTicks))
        {
            return true;
        }
        return false;
    }
    
    public static ImmutableList<QueueElement> GetQueueTimeoutElement(this ImmutableList<QueueElement> elements, long nowTicks)
    {
        var timeoutElements = ImmutableList.CreateBuilder<QueueElement>(); // Utilisation d'un builder pour créer une liste immuable
        foreach (var queueElement in elements)
        {
            if (queueElement.IsTimeout(nowTicks))
            {
                timeoutElements.Add(queueElement);
            }
        }
        return timeoutElements.ToImmutable(); // Conversion en ImmutableList
    }
    
    public static ImmutableList<QueueElement> GetQueueRunningElement(this ImmutableList<QueueElement> elements, long nowTicks)
    {
        var runningElements = ImmutableList.CreateBuilder<QueueElement>();
        foreach (var queueElement in elements)
        {
            if (queueElement.IsRunning(nowTicks))
            {
                runningElements.Add(queueElement);
            }
        }
        return runningElements.ToImmutable(); // Conversion en ImmutableList
    }
    
    public static ImmutableList<QueueElement> GetQueueWaitingForRetryElement(this ImmutableList<QueueElement> elements, long nowTicks)
    {
        var waitingForRetry = ImmutableList.CreateBuilder<QueueElement>();
        foreach (var queueElement in elements)
        {
            if (queueElement.IsWaitingForRetry(nowTicks))
            {
                waitingForRetry.Add(queueElement);
            }
        }
        return waitingForRetry.ToImmutable(); // Conversion en ImmutableList
    }
    
    public static ImmutableList<QueueElement> GetQueueAvailableElement(this ImmutableList<QueueElement> elements, long nowTicks, int maximum)
    {
        var runningElements = elements.GetQueueRunningElement(nowTicks);
        var runningWaitingForRetryElements = elements.GetQueueWaitingForRetryElement(nowTicks);
        var finishedElements = elements.GetQueueFinishedElement(nowTicks);
        var availableElements = ImmutableList.CreateBuilder<QueueElement>();
        var currentElements = elements.Except(runningElements).Except(runningWaitingForRetryElements).Except(finishedElements);
        var currentCount = 0;
       
        foreach (var queueElement in currentElements)
        {
            if (currentCount == maximum)
            {
                return availableElements.ToImmutable(); // Conversion en ImmutableList
            }
            availableElements.Add(queueElement);
            currentCount++;
        }
        return availableElements.ToImmutable(); // Conversion en ImmutableList
    }
    
    public static ImmutableList<QueueElement> GetQueueFinishedElement(this ImmutableList<QueueElement> elements, long nowTicks)
    {
        var queueFinishedElements = ImmutableList.CreateBuilder<QueueElement>();
        foreach (var queueElement in elements)
        {
            if (queueElement.IsFinished(nowTicks))
            {
                queueFinishedElements.Add(queueElement);
            }
        }
        return queueFinishedElements.ToImmutable(); // Conversion en ImmutableList
    }
}
