// Tests/Helpers/FakeHttpClientFactory.cs
using System.Net.Http;
using Microsoft.Extensions.Options;

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly HttpClient _client = new(handler) { BaseAddress = new Uri("https://api.example.com") };
    public HttpClient CreateClient(string? _) => _client;
}
