namespace SlimFaas.Kubernetes;

public record ReplicaRequest(string Deployment, string Namespace, int Replicas, PodType PodType);
