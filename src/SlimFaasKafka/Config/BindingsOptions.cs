namespace SlimFaasKafka.Config;

public sealed class BindingsOptions
{
    /// <summary>Liste des bindings topic -> fonction SlimFaas.</summary>
    public List<TopicBinding> Bindings { get; set; } = new();
}
