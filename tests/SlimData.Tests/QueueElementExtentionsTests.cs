using System.Collections.Immutable;

namespace SlimData.Tests;

public class QueueElementExtentionsTests
{
    [Fact]
    public static void QueueElementExtensionsGetQueueRunningElement()
    {
        // I want a test which text my extention
        var nowTicks = DateTime.UtcNow.Ticks;
        List<int> _retries = [2, 6, 10];
        var retries = _retries.ToImmutableList();
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
        var idTransaction = "";
        var timeoutSpanTicks = TimeSpan.FromSeconds(31).Ticks;
        ImmutableList<QueueElement> queueElements = ImmutableList<QueueElement>.Empty;
        var _httpRetriesCode = new List<int>(httpStatusCodesWorthRetrying) ;
        var httpRetriesCode = _httpRetriesCode.ToImmutableList();
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "-1", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks -100, idTransaction, nowTicks, 500),
            new(nowTicks -50, idTransaction, nowTicks, 500),
            new(nowTicks -20, idTransaction, nowTicks, 500),
            new(nowTicks -10, idTransaction, nowTicks, 500),
        }.ToImmutableList(), httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "0", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks - timeoutSpanTicks -100, idTransaction, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks -50, idTransaction, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks -30, idTransaction, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks -20,  idTransaction,0, 0),
        }.ToImmutableList(), httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "0-ok", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks  -100, idTransaction, nowTicks, 200),
        }.ToImmutableList(), httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "1", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks - 1000, idTransaction, nowTicks, 500),
            new(nowTicks- 500, idTransaction, nowTicks, 500),
            new(nowTicks- 200, idTransaction, nowTicks, 500),
            new(nowTicks- 100, idTransaction, 0, 0),
        }.ToImmutableList(), httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "1timeout", 090902, timeout, retries, new List<QueueHttpTryElement>()
        {
            new(nowTicks - 1000, idTransaction, nowTicks, 500),
            new(nowTicks- 500, idTransaction, nowTicks, 500),
            new(nowTicks- 400, idTransaction, nowTicks, 500),
            new(nowTicks- timeoutSpanTicks, idTransaction, 0, 0),
        }.ToImmutableList(), httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "2", 090902, timeout, retries, ImmutableList<QueueHttpTryElement>.Empty, httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "3", 090902, timeout, retries, ImmutableList<QueueHttpTryElement>.Empty, httpRetriesCode));
        queueElements = queueElements.Add(new QueueElement(new ReadOnlyMemory<byte>([1]), "4", 090902, timeout, retries, ImmutableList<QueueHttpTryElement>.Empty, httpRetriesCode));

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
