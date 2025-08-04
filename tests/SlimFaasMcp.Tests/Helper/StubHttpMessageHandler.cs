// Tests/Helpers/StubHttpMessageHandler.cs
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage,HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public int CallCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(responder(request));
    }
}

