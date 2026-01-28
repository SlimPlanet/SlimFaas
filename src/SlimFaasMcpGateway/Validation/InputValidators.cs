using System.Text.RegularExpressions;
using System.Globalization;

namespace SlimFaasMcpGateway.Api.Validation;

public static class InputValidators
{
    private static readonly Regex TenantNameRegex = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,255}$", RegexOptions.Compiled);

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    public static void ValidateTenantName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ApiException(400, "Tenant name is required.");
        if (name.Length > 256) throw new ApiException(400, "Tenant name must be <= 256 characters.");
        if (!TenantNameRegex.IsMatch(name)) throw new ApiException(400, "Tenant name contains invalid characters.");
    }

    public static void ValidateConfigurationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ApiException(400, "Configuration name is required.");
        if (name.Length > 256) throw new ApiException(400, "Configuration name must be <= 256 characters.");
    }

    public static void ValidateDescription(string? desc)
    {
        if (desc is not null && desc.Length > 1000) throw new ApiException(400, "Description must be <= 1000 characters.");
    }

    public static void ValidateAbsoluteHttpUrl(string url, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ApiException(400, $"{fieldName} is required.");
        
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ApiException(400, $"{fieldName} must be an absolute URL. Provided: '{url}'");
        }
        
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ApiException(400, $"{fieldName} must be http or https. Scheme detected: '{uri.Scheme}'");
        }
    }

    public static void ValidateCatalogCacheTtl(int ttlMinutes)
    {
        if (ttlMinutes < 0) throw new ApiException(400, "CatalogCacheTtlMinutes must be >= 0.");
    }

    public static void ValidateYamlIfPresent(string? yaml, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return;
        try
        {
            _ = SimpleYaml.Parse(yaml);
        }
        catch (Exception ex)
        {
            throw new ApiException(400, $"{fieldName} is not valid YAML: {ex.Message}");
        }
    }

    public static void RequireYamlWhenEnabled(bool enabled, string? yaml, string fieldName)
    {
        if (!enabled) return;
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ApiException(400, $"{fieldName} is required when enabled.");
    }
}
