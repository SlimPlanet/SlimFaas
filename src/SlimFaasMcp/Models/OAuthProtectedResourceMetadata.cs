using System.Text.Json.Serialization;

namespace SlimFaasMcp.Models;

/// <summary>
/// Représentation stricte de la section 2 du RFC 9728 (cf. lignes 18-27) .
/// Les champs optionnels sont marqués « ? » et [StringLength] ou [Url]
/// peuvent être ajoutés si vous voulez pousser la validation.
/// </summary>
public class OAuthProtectedResourceMetadata
{
    [JsonPropertyName("resource")]                                         // REQUIRED
    public string Resource { get; set; } = default!;

    [JsonPropertyName("authorization_servers")]
    public List<string>? AuthorizationServers { get; set; }

    [JsonPropertyName("jwks_uri")]                                         // OPTIONAL
    public string? JwksUri { get; set; }

    [JsonPropertyName("scopes_supported")]
    public List<string>? ScopesSupported { get; set; }

    [JsonPropertyName("bearer_methods_supported")]
    public List<string>? BearerMethodsSupported { get; set; }

    [JsonPropertyName("resource_signing_alg_values_supported")]
    public List<string>? ResourceSigningAlgValuesSupported { get; set; }

    [JsonPropertyName("resource_name")]
    public string? ResourceName { get; set; }

    [JsonPropertyName("resource_documentation")]
    public string? ResourceDocumentation { get; set; }

    [JsonPropertyName("resource_policy_uri")]
    public string? ResourcePolicyUri { get; set; }

    [JsonPropertyName("resource_tos_uri")]
    public string? ResourceTosUri { get; set; }

    [JsonPropertyName("tls_client_certificate_bound_access_tokens")]
    public bool? TlsClientCertBoundAccessTokens { get; set; }

    [JsonPropertyName("authorization_details_types_supported")]
    public List<string>? AuthorizationDetailsTypesSupported { get; set; }

    [JsonPropertyName("dpop_signing_alg_values_supported")]
    public List<string>? DPoPSigningAlgValuesSupported { get; set; }

    [JsonPropertyName("dpop_bound_access_tokens_required")]
    public bool? DPoPBoundAccessTokensRequired { get; set; }

    // Attrape-tout pour d’éventuels futurs champs
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
