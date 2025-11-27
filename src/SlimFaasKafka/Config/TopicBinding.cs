namespace SlimFaasKafka.Config;

public sealed class TopicBinding
{
    /// <summary>Nom du topic Kafka à surveiller.</summary>
    public required string Topic { get; init; }

    /// <summary>
    /// Consumer group dont on veut mesurer le lag (celui utilisé par les fonctions SlimFaas).
    /// </summary>
    public required string ConsumerGroupId { get; init; }

    /// <summary>Nom de la fonction SlimFaas à réveiller.</summary>
    public required string FunctionName { get; init; }

    /// <summary>Nombre minimal de messages en attente pour déclencher un wake up.</summary>
    public int MinPendingMessages { get; init; } = 1;

    /// <summary>
    /// Cooldown minimal entre deux wake up pour ce binding (en secondes),
    /// que ce soit pour du lag ou pour de l'activité récente.
    /// </summary>
    public int CooldownSeconds { get; init; } = 30;

    /// <summary>
    /// Durée pendant laquelle une activité récente (messages consommés)
    /// doit maintenir des wake-ups réguliers (en secondes).
    /// 0 ou valeur négative => désactivé.
    /// </summary>
    public int ActivityKeepAliveSeconds { get; init; } = 60;

    /// <summary>
    /// Nombre minimal de messages consommés (delta d'offset) pour considérer
    /// qu'il y a eu de l'activité (déclenchement / refresh du keep-alive).
    /// </summary>
    public long MinConsumedDeltaForActivity { get; init; } = 1;
}
