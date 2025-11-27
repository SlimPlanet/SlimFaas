namespace SlimFaasKafka.Config;

public sealed class SlimFaasOptions
{
    /// <summary>Base URL de SlimFaas (ex: "http://slimfaas:5000").</summary>
    public string BaseUrl { get; set; } = "http://slimfaas:5000";

    /// <summary>Template de chemin pour le wake up. {functionName} sera remplacé.</summary>
    /// <example>"/api/functions/{functionName}/wake"</example>
    public string WakeUpPathTemplate { get; set; } = "/api/functions/{functionName}/wake";

    /// <summary>Timeout HTTP vers SlimFaas (en secondes).</summary>
    public int HttpTimeoutSeconds { get; set; } = 10;
}
