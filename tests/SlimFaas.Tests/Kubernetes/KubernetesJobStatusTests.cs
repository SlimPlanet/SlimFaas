using k8s.Models;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class KubernetesJobStatusTests
{
    [Theory]
    [InlineData("Complete", JobStatus.Succeeded)]
    [InlineData("Failed", JobStatus.Failed)]
    public void TerminalCondition_DeterminesFinishedStatus(string condition, JobStatus expected)
    {
        var status = new V1JobStatus
        {
            Conditions = [new V1JobCondition { Type = condition, Status = "True" }]
        };

        Assert.Equal(expected, KubernetesService.ResolveJobStatus(status, []));
    }

    [Theory]
    [InlineData(1, 0, 1, JobStatus.Running)]
    [InlineData(0, 0, 1, JobStatus.Pending)]
    [InlineData(1, 1, 0, JobStatus.Running)]
    [InlineData(0, 1, 0, JobStatus.Pending)]
    public void PodCounters_DoNotMakeAnUnfinishedJobTerminal(int active, int succeeded, int failed, JobStatus expected)
    {
        var status = new V1JobStatus { Active = active, Succeeded = succeeded, Failed = failed };

        Assert.Equal(expected, KubernetesService.ResolveJobStatus(status, []));
    }

    [Theory]
    [InlineData("Complete", "False")]
    [InlineData("Failed", "Unknown")]
    [InlineData("FailureTarget", "True")]
    [InlineData("SuccessCriteriaMet", "True")]
    public void NonterminalCondition_DoesNotReleaseSlot(string condition, string value)
    {
        var status = new V1JobStatus
        {
            Active = 1,
            Conditions = [new V1JobCondition { Type = condition, Status = value }]
        };

        Assert.Equal(JobStatus.Running, KubernetesService.ResolveJobStatus(status, []));
    }

    [Theory]
    [InlineData("Complete", JobStatus.Succeeded)]
    [InlineData("Failed", JobStatus.Failed)]
    public void TerminalCondition_TakesPrecedenceOverPodErrors(string condition, JobStatus expected)
    {
        var status = new V1JobStatus
        {
            Conditions = [new V1JobCondition { Type = condition, Status = "True" }],
            Succeeded = condition == "Complete" ? 1 : 0,
            Failed = condition == "Failed" ? 1 : 0
        };

        Assert.Equal(expected, KubernetesService.ResolveJobStatus(status, [PullErrorPod("ImagePullBackOff")]));
    }

    [Theory]
    [InlineData("ImagePullBackOff")]
    [InlineData("ErrImagePull")]
    public void UnfinishedJob_PreservesImagePullStatus(string reason)
    {
        Assert.Equal(JobStatus.ImagePullBackOff,
            KubernetesService.ResolveJobStatus(new V1JobStatus(), [PullErrorPod(reason)]));
    }

    [Fact]
    public void MissingStatus_IsPending()
    {
        Assert.Equal(JobStatus.Pending, KubernetesService.ResolveJobStatus(null, []));
        Assert.Equal(JobStatus.Pending, KubernetesService.ResolveJobStatus(new V1JobStatus(),
            [new V1Pod(), new V1Pod { Status = new V1PodStatus() }]));
    }

    private static V1Pod PullErrorPod(string reason) => new()
    {
        Status = new V1PodStatus
        {
            ContainerStatuses = [new V1ContainerStatus
            {
                State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = reason } }
            }]
        }
    };
}
