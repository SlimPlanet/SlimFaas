using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimFaas.Options;
using SlimFaas.Security;

namespace SlimFaas.Tests.Security;

public class TrustedProxiesTests
{
    [Fact]
    public void CreateForwardedHeadersOptions_WithoutProxies_ReturnsNull()
    {
        Assert.Null(TrustedProxies.CreateForwardedHeadersOptions(null));
        Assert.Null(TrustedProxies.CreateForwardedHeadersOptions([]));
        Assert.Null(TrustedProxies.CreateForwardedHeadersOptions(["", "  "]));
    }

    [Fact]
    public void CreateForwardedHeadersOptions_OnlyTrustsTheDeclaredProxies_OneHop()
    {
        var options = TrustedProxies.CreateForwardedHeadersOptions(["10.0.0.5", " 10.244.0.0/16 ", "fd00::/8"]);

        Assert.NotNull(options);
        Assert.Equal(ForwardedHeaders.XForwardedFor, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], options.KnownProxies);
        Assert.Equal(2, options.KnownIPNetworks.Count);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, options.KnownProxies);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.5:8080")]
    public void CreateForwardedHeadersOptions_RejectsInvalidEntries(string entry)
    {
        Assert.False(TrustedProxies.TryParse([entry], out _, out _, out string? invalid));
        Assert.Equal(entry, invalid);
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.CreateForwardedHeadersOptions([entry]));
    }

    [Fact]
    public void SlimFaasOptions_ValidationRejectsInvalidTrustedProxies()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimFaas:TrustedProxies:0"] = "10.0.0.5",
            ["SlimFaas:TrustedProxies:1"] = "not-an-ip"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSlimFaasOptions(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SlimFaasOptions>>().Value);
        Assert.Contains("TrustedProxies", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.1", "10.0.0.1")] // declared proxy: forwarded client is used
    [InlineData("10.0.0.5", "10.0.0.1, 10.0.0.2", "10.0.0.2")] // one hop only: the last entry
    [InlineData("10.0.0.6", "10.0.0.1", "10.0.0.6")] // unknown proxy: header ignored
    [InlineData("203.0.113.7", "10.0.0.1", "203.0.113.7")] // external caller forging the header
    public async Task ForwardedHeaders_ReplaceTheRemoteAddressOnlyBehindADeclaredProxy(
        string connectionIp, string forwardedFor, string expectedRemoteIp)
    {
        var options = TrustedProxies.CreateForwardedHeadersOptions(["10.0.0.5"]);
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .Configure(app =>
            {
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(connectionIp);
                    return next(context);
                });
                app.UseForwardedHeaders(options!);
                app.Run(context => context.Response.WriteAsync(context.Connection.RemoteIpAddress!.ToString()));
            })).StartAsync();

        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        Assert.Equal(expectedRemoteIp, await client.GetStringAsync(new Uri("http://localhost/")));
    }
}
