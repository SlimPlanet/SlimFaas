namespace SlimFaas.Kubernetes;

public enum FunctionVisibility
{
    Public,
    Private
}

public enum FunctionTrust
{
    Trusted,
    Untrusted
}

public enum PodType
{
    Deployment,
    StatefulSet,
    WebSocket
}

public record SubscribeEvent(string Name, FunctionVisibility Visibility);

public record PathVisibility(string Path, FunctionVisibility Visibility);
