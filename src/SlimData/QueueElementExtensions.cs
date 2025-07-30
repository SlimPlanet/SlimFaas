namespace SlimData;

public static class QueueElementExtensions
{

    
    public static bool IsTimeout(this QueueElement element, long nowTicks)
    {
        if (element.RetryQueueElements.Count <= 0) return false;
        int timeout=element.HttpTimeout;
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
        List<int> retries= element.TimeoutRetries;
        var count = element.RetryQueueElements.Count;
        if(count == 0 || count > retries.Count ) return false;
        
        if(element.IsFinished(nowTicks)) return false;

        if(element.IsRunning(nowTicks)) return false;
        
        var retryQueueElement = element.RetryQueueElements[^1];
        var retryTimeout = retries[count - 1];
        if(element.IsTimeout(nowTicks) && TimeSpan.FromSeconds(retryTimeout).Ticks + element.HttpTimeout > nowTicks - retryQueueElement.StartTimeStamp)
        {
            return true;
        }

        if (retryQueueElement.EndTimeStamp != 0 &&
            ( TimeSpan.FromSeconds(retryTimeout).Ticks > nowTicks - retryQueueElement.EndTimeStamp))
        {
            return true;
        }
        
        return false;
    }
    
    public static bool IsFinished(this QueueElement queueElement, long nowTicks)
    {
        var count = queueElement.RetryQueueElements.Count;
        if (count <= 0) return false;
        List<int> retries= queueElement.TimeoutRetries;
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
    
    public static List<QueueElement> GetQueueTimeoutElement(this List<QueueElement> element, long nowTicks)
    {
        var timeoutElements = new List<QueueElement>();
        foreach (var queueElement in element)
        {
            if(queueElement.IsTimeout(nowTicks))
            {
                timeoutElements.Add(queueElement);
            }
           
        }
        return timeoutElements;
    }
    
    public static List<QueueElement> GetQueueRunningElement(this List<QueueElement> element, long nowTicks)
    {
        var runningElement = new List<QueueElement>();
        foreach (var queueElement in element)
        {
            if(queueElement.IsRunning(nowTicks))
            {
                runningElement.Add(queueElement);
            }
        }
        return runningElement;
    }
    
    public static List<QueueElement> GetQueueWaitingForRetryElement(this List<QueueElement> element, long nowTicks)
    {
        var waitingForRetry = new List<QueueElement>();
        foreach (var queueElement in element)
        {
            if (queueElement.IsWaitingForRetry(nowTicks))
            {
                waitingForRetry.Add(queueElement);
            }
        }
        return waitingForRetry;
    }
    
    public static List<QueueElement> GetQueueAvailableElement(this List<QueueElement> elements, long nowTicks, int maximum)
    {
        var runningElements = elements.GetQueueRunningElement(nowTicks);
        var runningWaitingForRetryElements = elements.GetQueueWaitingForRetryElement(nowTicks);
        var finishedElements = elements.GetQueueFinishedElement(nowTicks);
        var availableElements = new List<QueueElement>();
        var currentElements = elements.Except(runningElements).Except(runningWaitingForRetryElements).Except(finishedElements);
        var currentCount = 0;
       
        foreach (var queueElement in currentElements)
        {
            if (currentCount == maximum)
            {
                return availableElements;
            }
            availableElements.Add(queueElement);
            currentCount++;
        }
        return availableElements;
    }
    
    public static List<QueueElement> GetQueueFinishedElement(this List<QueueElement> element, long nowTicks)
    {
        var queueFinishedElements = new List<QueueElement>();
        foreach (var queueElement in element)
        {
            if (queueElement.IsFinished(nowTicks))
            {
                queueFinishedElements.Add(queueElement);
            }
           
        }
        return queueFinishedElements;
    }

}