namespace SlimData.Options;

public class RaftClientHandlerOptions
{
    public const string SectionName = "RaftClientHandler";
    
    /// <summary>
    /// Timeout en millisecondes pour l'établissement de connexion TCP+TLS.
    /// Valeur par défaut : 2000 ms (2 secondes).
    /// </summary>
    public int ConnectTimeoutMilliseconds { get; set; } = 2000;
    
    /// <summary>
    /// Durée de vie des connexions poolées.
    /// Valeur par défaut : 5 minutes.
    /// </summary>
    public int PooledConnectionLifetimeMinutes { get; set; } = 5;
    
    /// <summary>
    /// Timeout d'inactivité des connexions poolées.
    /// Valeur par défaut : 30 secondes.
    /// </summary>
    public int PooledConnectionIdleTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Nombre maximum de connexions par serveur.
    /// Valeur par défaut : 100.
    /// </summary>
    public int MaxConnectionsPerServer { get; set; } = 100;
}
