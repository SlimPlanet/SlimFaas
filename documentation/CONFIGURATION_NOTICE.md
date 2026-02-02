# Configuration Migration Notice

⚠️ **Important**: SlimFaas now uses strongly-typed configuration through `appsettings.json` instead of environment variables.

## Quick Migration Guide

### Old Way (No Longer Supported)
```bash
export SLIMFAAS_CORS_ALLOW_ORIGIN="https://myapp.com"
export NAMESPACE="production"
export SLIM_WORKER_DELAY_MILLISECONDS=20
```

### New Way (Option 1 - appsettings.json)
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

### New Way (Option 2 - Environment Variables)
```bash
# Use double underscore to separate sections
export SlimFaas__CorsAllowOrigin="https://myapp.com"
export SlimFaas__Namespace="production"
export Workers__DelayMilliseconds=20
```

### Kubernetes ConfigMap
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

See `MIGRATION_CONFIGURATION.md` for the complete guide.

---
