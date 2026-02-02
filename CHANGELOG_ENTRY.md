# CHANGELOG Entry - Configuration Refactoring

## [Unreleased] - 2026-01-31

### ⚠️ BREAKING CHANGES

#### Configuration System Refactored to Use Strongly-Typed Options

**Overview:**
SlimFaas has completely migrated from environment variables to strongly-typed configuration using `appsettings.json`. This provides better type safety, validation, and follows .NET 10 best practices.

**Migration Required:**
All users must update their configuration. See `MIGRATION_CONFIGURATION.md` for the complete migration guide.

**Old Way (No Longer Supported):**
```bash
export SLIMFAAS_CORS_ALLOW_ORIGIN="https://myapp.com"
export NAMESPACE="production"
export SLIM_WORKER_DELAY_MILLISECONDS=20
```

**New Way (Option 1 - appsettings.json):**
```json
{
  "SlimFaas": {
    "CorsAllowOrigin": "https://myapp.com",
    "Namespace": "production"
  },
  "Workers": {
    "DelayMilliseconds": 20
  }
}
```

**New Way (Option 2 - Environment Variables Override):**
```bash
export SlimFaas__CorsAllowOrigin="https://myapp.com"
export SlimFaas__Namespace="production"
export Workers__DelayMilliseconds=20
```

### Added

- **New Options Classes:**
  - `SlimFaasOptions` - Main SlimFaas configuration
  - `SlimDataOptions` - SlimData-specific configuration
  - `WorkersOptions` - Background workers configuration
  - `OptionsExtensions` - Helper methods for configuration

- **Documentation:**
  - `MIGRATION_CONFIGURATION.md` - Complete migration guide
  - `REFACTORING_SUMMARY.md` - Technical details
  - `REFACTORING_COMPLETE.md` - Overview and completion status
  - `docker-compose.example.yml` - Docker Compose example
  - `kubernetes-example.yml` - Kubernetes ConfigMap example

### Changed

- **Program.cs** - Completely refactored to use strongly-typed options
- **All Workers** - Updated to inject `IOptions<WorkersOptions>` and `IOptions<SlimFaasOptions>`
- **Services** - Updated to use dependency injection for configuration:
  - `SendClient`
  - `SlimFaasPorts`
  - `JobService`
  - `JobConfiguration`
  - `MockKubernetesService`
- **appsettings.json** - New configuration sections added
- **global.json** - SDK version updated to 10.0.100

### Deprecated

- `EnvironmentVariables.cs` - No longer used, can be removed in future version

### Benefits

1. **Type Safety** - Configuration errors caught at compile time
2. **Validation** - Automatic validation at startup
3. **IntelliSense** - Full IDE support for configuration
4. **Testability** - Easy to mock in unit tests
5. **Maintainability** - Cleaner, more organized code
6. **AOT Compatibility** - Better support for Native AOT
7. **Standards** - Follows Microsoft .NET best practices

### Migration Guide

**Quick Start:**

1. Create or update your `appsettings.json`:
```json
{
  "SlimFaas": {
    "Namespace": "your-namespace",
    "CorsAllowOrigin": "*"
  }
}
```

2. Or use environment variables with new format:
```bash
export SlimFaas__Namespace=your-namespace
export SlimFaas__CorsAllowOrigin=*
```

**For Docker Compose:**
```yaml
services:
  slimfaas:
    environment:
      - SlimFaas__Namespace=default
      - SlimFaas__Orchestrator=Docker
```

**For Kubernetes:**
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: slimfaas-config
data:
  appsettings.Production.json: |
    {
      "SlimFaas": {
        "Namespace": "production"
      }
    }
```

See `MIGRATION_CONFIGURATION.md` for complete mapping table and detailed instructions.

### Configuration Mapping

| Old Environment Variable | New Configuration Path | Default |
|-------------------------|------------------------|---------|
| `SLIMFAAS_ALLOW_UNSECURE_SSL` | `SlimFaas:AllowUnsecureSsl` | `false` |
| `SLIMFAAS_CORS_ALLOW_ORIGIN` | `SlimFaas:CorsAllowOrigin` | `"*"` |
| `BASE_SLIMDATA_URL` | `SlimFaas:BaseSlimDataUrl` | `"http://{pod_name}.{service_name}.{namespace}.svc:3262"` |
| `BASE_FUNCTION_URL` | `SlimFaas:BaseFunctionUrl` | `"http://{pod_ip}:{pod_port}"` |
| `NAMESPACE` | `SlimFaas:Namespace` | `"default"` |
| `SLIMFAAS_ORCHESTRATOR` | `SlimFaas:Orchestrator` | `"Kubernetes"` |
| `SLIM_WORKER_DELAY_MILLISECONDS` | `Workers:DelayMilliseconds` | `10` |
| `HEALTH_WORKER_DELAY_MILLISECONDS` | `Workers:HealthDelayMilliseconds` | `1000` |
| `SLIMDATA_DIRECTORY` | `SlimData:Directory` | `null` |

*See `MIGRATION_CONFIGURATION.md` for complete table*

### Notes

- The old environment variable names are **no longer supported**
- Configuration is validated at startup - invalid configuration will cause the application to fail to start
- The `HOSTNAME` environment variable is still read from the system (set by Kubernetes/Docker)
- Namespace can still be auto-detected from Kubernetes service account

### Support

If you encounter issues during migration:
1. Check `MIGRATION_CONFIGURATION.md` for detailed examples
2. Review `REFACTORING_SUMMARY.md` for technical details
3. Open an issue on GitHub with your configuration

---

**Full documentation:** See `MIGRATION_CONFIGURATION.md`, `REFACTORING_SUMMARY.md`, and `REFACTORING_COMPLETE.md`
