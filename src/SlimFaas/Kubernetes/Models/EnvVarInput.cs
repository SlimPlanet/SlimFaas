using MemoryPack;

namespace SlimFaas.Kubernetes;

[MemoryPackable]
public partial record EnvVarInput(
    string Name,
    string Value,
    SecretRef? SecretRef = null,
    ConfigMapRef? ConfigMapRef = null,
    FieldRef? FieldRef = null,
    ResourceFieldRef? ResourceFieldRef = null)
{
    public string Name { get; set; } = Name;

    public string Value { get; set; } = Value;

    public SecretRef? SecretRef { get; set; } = SecretRef;

    public ConfigMapRef? ConfigMapRef { get; set; } = ConfigMapRef;

    public FieldRef? FieldRef { get; set; } = FieldRef;

    public ResourceFieldRef? ResourceFieldRef { get; set; } = ResourceFieldRef;
}

[MemoryPackable]
public partial record SecretRef(string Name, string Key)
{
    public string Name { get; set; } = Name;
    public string Key { get; set; } = Key;
}

[MemoryPackable]
public partial record ConfigMapRef(string Name, string Key)
{
    public string Name { get; set; } = Name;
    public string Key { get; set; } = Key;
}

[MemoryPackable]
public partial record FieldRef(string FieldPath)
{
    public string FieldPath { get; set; } = FieldPath;
}

[MemoryPackable]
public partial record ResourceFieldRef(string ContainerName, string Resource, string Divisor)
{
    public string ContainerName { get; set; } = ContainerName;
    public string Resource { get; set; } = Resource;
    public string Divisor { get; set; } = Divisor;
}

[MemoryPackable]
public partial record CreateJobResources(Dictionary<string, string> Requests, Dictionary<string, string> Limits);
