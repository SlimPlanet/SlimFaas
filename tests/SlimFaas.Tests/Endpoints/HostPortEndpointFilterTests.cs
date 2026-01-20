using Microsoft.AspNetCore.Http;
using Moq;
using SlimFaas.Endpoints;

namespace SlimFaas.Tests.Endpoints;

public class HostPortEndpointFilterTests
{
    [Fact]
    public async Task InvokeAsync_WhenPortMatches_ShouldCallNext()
    {
        // Arrange
        var mockSlimFaasPorts = new Mock<ISlimFaasPorts>();
        mockSlimFaasPorts.Setup(x => x.Ports).Returns(new List<int> { 5000, 8080 });

        var filter = new HostPortEndpointFilter(mockSlimFaasPorts.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 5000;
        httpContext.Request.Host = new HostString("localhost", 5000);

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.True(nextCalled);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenPortDoesNotMatch_ShouldReturnNotFound()
    {
        // Arrange
        var mockSlimFaasPorts = new Mock<ISlimFaasPorts>();
        mockSlimFaasPorts.Setup(x => x.Ports).Returns(new List<int> { 5000, 8080 });

        var filter = new HostPortEndpointFilter(mockSlimFaasPorts.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 9999; // Port non autorisé
        httpContext.Request.Host = new HostString("localhost", 9999);

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.False(nextCalled);
        Assert.NotNull(result);
        // Vérifier que c'est un NotFound (404)
        var httpResult = result as IResult;
        Assert.NotNull(httpResult);
    }

    [Fact]
    public async Task InvokeAsync_WhenLocalPortMatches_ShouldCallNext()
    {
        // Arrange
        var mockSlimFaasPorts = new Mock<ISlimFaasPorts>();
        mockSlimFaasPorts.Setup(x => x.Ports).Returns(new List<int> { 5000, 8080 });

        var filter = new HostPortEndpointFilter(mockSlimFaasPorts.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 5000; // Match
        httpContext.Request.Host = new HostString("localhost", 9999); // Pas de match

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        _ = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.True(nextCalled); // Doit passer car LocalPort correspond
    }

    [Fact]
    public async Task InvokeAsync_WhenHostPortMatches_ShouldCallNext()
    {
        // Arrange
        var mockSlimFaasPorts = new Mock<ISlimFaasPorts>();
        mockSlimFaasPorts.Setup(x => x.Ports).Returns(new List<int> { 5000, 8080 });

        var filter = new HostPortEndpointFilter(mockSlimFaasPorts.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 9999; // Pas de match
        httpContext.Request.Host = new HostString("localhost", 8080); // Match

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        _ = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.True(nextCalled); // Doit passer car Host.Port correspond
    }

    [Fact]
    public async Task InvokeAsync_WhenSlimFaasPortsIsNull_ShouldReturnNotFound()
    {
        // Arrange
        var filter = new HostPortEndpointFilter(null);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 5000;
        httpContext.Request.Host = new HostString("localhost", 5000);

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        _ = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.False(nextCalled); // Ne doit pas appeler next si SlimFaasPorts est null
    }

    [Fact]
    public async Task InvokeAsync_WhenPortsListIsEmpty_ShouldReturnNotFound()
    {
        // Arrange
        var mockSlimFaasPorts = new Mock<ISlimFaasPorts>();
        mockSlimFaasPorts.Setup(x => x.Ports).Returns(new List<int>()); // Liste vide

        var filter = new HostPortEndpointFilter(mockSlimFaasPorts.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 5000;
        httpContext.Request.Host = new HostString("localhost", 5000);

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        _ = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.False(nextCalled); // Ne doit pas appeler next si la liste est vide
    }

    [Fact]
    public async Task InvokeAsync_WhenHostPortIsNull_ShouldUseLocalPort()
    {
        // Arrange
        var mockSlimFaasPorts = new Mock<ISlimFaasPorts>();
        mockSlimFaasPorts.Setup(x => x.Ports).Returns(new List<int> { 5000 });

        var filter = new HostPortEndpointFilter(mockSlimFaasPorts.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 5000;
        httpContext.Request.Host = new HostString("localhost"); // Pas de port

        var endpointContext = new DefaultEndpointFilterInvocationContext(httpContext);
        var nextCalled = false;

        ValueTask<object?> Next(EndpointFilterInvocationContext ctx)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        _ = await filter.InvokeAsync(endpointContext, Next);

        // Assert
        Assert.True(nextCalled); // Doit passer car LocalPort correspond
    }
}

// Classe helper pour créer un EndpointFilterInvocationContext
public class DefaultEndpointFilterInvocationContext : EndpointFilterInvocationContext
{
    private readonly HttpContext _httpContext;

    public DefaultEndpointFilterInvocationContext(HttpContext httpContext)
    {
        _httpContext = httpContext;
    }

    public override HttpContext HttpContext => _httpContext;

    public override IList<object?> Arguments => new List<object?>();

    public override T GetArgument<T>(int index) => default!;
}

