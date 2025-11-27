namespace SlimFaasKafka.Config;

public sealed class KafkaOptions
{
    /// <summary>Liste des brokers Kafka (ex: "kafka:9092").</summary>
    public string BootstrapServers { get; set; } = "kafka:9092";

    /// <summary>Nom d'application côté client (facultatif).</summary>
    public string ClientId { get; set; } = "slimkafka-monitor";

    /// <summary>Intervalle entre deux checks (en secondes).</summary>
    public int CheckIntervalSeconds { get; set; } = 5;

    /// <summary>Timeout pour les appels Kafka (en secondes).</summary>
    public int KafkaTimeoutSeconds { get; set; } = 5;

    /// <summary>Active l'auto création de topics côté client (si autorisé au niveau cluster).</summary>
    public bool AllowAutoCreateTopics { get; set; } = true;
}
