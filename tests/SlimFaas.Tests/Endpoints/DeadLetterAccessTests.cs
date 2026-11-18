using DotNext;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas.DeadLetters;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public sealed class DeadLetterAccessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Front_and_authorization_are_independent_and_private_by_default(bool globalFront, bool dlqFront)
    {
        var access = new Mock<IFunctionAccessPolicy>();
        var options = new SlimFaasOptions { EnableFront = globalFront, ExposeDataMetadata = true };
        options.DeadLetterQueue.EnableFront = dlqFront;
        var service = new DeadLetterService(new Mock<IDatabaseService>().Object,
            new Mock<ISupplier<SlimDataPayload>>().Object, new Mock<IReplicasService>().Object,
            new Mock<IWebSocketFunctionRepository>().Object, access.Object, Microsoft.Extensions.Options.Options.Create(options),
            new Mock<IClusterFileSync>().Object, new Mock<IMasterService>().Object, new NetworkActivityTracker(),
            NullLogger<DeadLetterService>.Instance);
        using (service)
        {
            var context = new DefaultHttpContext();
            Assert.False(service.CanAccess(context));
            Assert.Equal(globalFront && dlqFront, service.FrontEnabled);
            access.Setup(value => value.IsInternalRequest(context)).Returns(true);
            Assert.True(service.CanAccess(context));
            access.Setup(value => value.IsInternalRequest(context)).Returns(false);
            options.DeadLetterQueue.DefaultVisibility = FunctionVisibility.Public;
            Assert.True(service.CanAccess(context));
        }
    }
}
