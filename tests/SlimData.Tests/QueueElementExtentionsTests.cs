﻿namespace SlimData.Tests;

public class QueueElementExtentionsTests
{
    [Fact]
    public static void QueueElementExtensionsGetQueueRunningElement()
    {
        // I want a test which text my extention
        var nowTicks = DateTime.UtcNow.Ticks;
        List<int> retries = [2, 6, 10];
        int retryTimeout = 30;
        int[] httpStatusCodesWorthRetrying =
            [
                // 408 , // HttpStatusCode.RequestTimeout,
                500, // HttpStatusCode.InternalServerError,
                502, // HttpStatusCode.BadGateway,
                503, // HttpStatusCode.ServiceUnavailable,
                //504, // HttpStatusCode.GatewayTimeout
            ];

        var timeout = 30;
        var timeoutSpanTicks = TimeSpan.FromSeconds(31).Ticks;
        List<QueueElement> queueElements = new();
        var httpRetriesCode = new List<int>(httpStatusCodesWorthRetrying) ;
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "-1", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks -100, nowTicks, 500),
            new(nowTicks -50, nowTicks, 500),
            new(nowTicks -20, nowTicks, 500),
            new(nowTicks -10, nowTicks, 500),
        }, httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "0", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks - timeoutSpanTicks -100, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks -50, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks -30, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks -20, 0, 0),
        }, httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "0-ok", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks  -100, nowTicks, 200),
        }, httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "1", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks - 1000, nowTicks, 500),
            new(nowTicks- 500, nowTicks, 500),
            new(nowTicks- 200, nowTicks, 500),
            new(nowTicks- 100, 0, 0),
        }, httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "1timeout", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks - 1000, nowTicks, 500),
            new(nowTicks- 500, nowTicks, 500),
            new(nowTicks- 400, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks, 0, 0),
        }, httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "2", 090902, timeout, retries, new List<QueueHttpTryElement>(), httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "3", 090902, timeout, retries, new List<QueueHttpTryElement>(), httpRetriesCode));
        queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "4", 090902, timeout, retries, new List<QueueHttpTryElement>(), httpRetriesCode));

        var availableElements = queueElements.GetQueueAvailableElement(nowTicks, 3);

        Assert.Equal(3, availableElements.Count);
        Assert.Equal("2", availableElements[0].Id);
        Assert.Equal("3", availableElements[1].Id);

        var runningElements = queueElements.GetQueueRunningElement(nowTicks);
        Assert.Equal(1, runningElements.Count);
        Assert.Equal("1", runningElements[0].Id);


        var finishedElements = queueElements.GetQueueFinishedElement(nowTicks);
        Assert.Equal(4, finishedElements.Count);
    }

}
