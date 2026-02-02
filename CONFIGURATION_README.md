# Configuration Refactoring - Quick Reference

## 🚀 Quick Start

### For Development
```bash
# No changes needed - defaults work out of the box
cd src/SlimFaas
dotnet run
```

### For Production with appsettings.json
```bash
# Edit appsettings.Production.json
{
  "SlimFaas": {
    "Namespace": "production",
    "CorsAllowOrigin": "https://myapp.com"
  }
}

# Run with production settings
export ASPNETCORE_ENVIRONMENT=Production
dotnet run
```

### For Production with Environment Variables
```bash
# Use double underscore for nested properties
export SlimFaas__Namespace=production
export SlimFaas__CorsAllowOrigin=https://myapp.com
export Workers__DelayMilliseconds=20
dotnet run
```

## 📖 Documentation Files

1. **`MIGRATION_CONFIGURATION.md`** 👥 For Users
   - Step-by-step migration guide
   - Complete mapping table
   - Kubernetes & Docker Compose examples

2. **`REFACTORING_SUMMARY.md`** 👨‍💻 For Developers
   - Technical implementation details
   - List of all modified files
   - Architecture changes

3. **`REFACTORING_COMPLETE.md`** 📊 Overview
   - Executive summary
   - Statistics and metrics
   - Next steps

4. **`CHANGELOG_ENTRY.md`** 📝 For Release Notes
   - Ready-to-use CHANGELOG entry
   - Breaking change announcement
   - Quick reference

## 🔧 Configuration Structure

```
SlimFaas/
├── SlimFaasOptions          # Main configuration
│   ├── Namespace            # Kubernetes namespace
│   ├── Orchestrator         # Kubernetes/Docker/Mock
│   ├── CorsAllowOrigin      # CORS settings
│   ├── AllowUnsecureSsl     # SSL validation
│   ├── BaseSlimDataUrl      # SlimData URL template
│   ├── BaseFunctionUrl      # Function URL template
│   └── ...
│
├── SlimDataOptions          # SlimData specific
│   ├── Directory            # Storage directory
│   ├── Configuration        # SlimData config JSON
│   └── AllowColdStart       # Cold start setting
│
└── WorkersOptions           # Background workers
    ├── DelayMilliseconds    # Main worker delay
    ├── JobsDelayMilliseconds
    ├── HealthDelayMilliseconds
    └── ...
```

## 🎯 Common Scenarios

### Docker Development
```yaml
# docker-compose.yml
services:
  slimfaas:
    environment:
      - SlimFaas__Orchestrator=Docker
      - SlimFaas__AllowUnsecureSsl=true
```

### Kubernetes Production
```yaml
# configmap.yaml
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

### Local Testing with Mock
```json
// appsettings.Development.json
{
  "SlimFaas": {
    "Orchestrator": "Mock",
    "MockKubernetesFunctions": "{\"Functions\":[],\"Slimfaas\":[]}"
  }
}
```

## ✅ Validation

Configuration is validated at startup. Invalid values will cause the application to fail with clear error messages.

## 🆘 Need Help?

1. Check the detailed migration guide: `MIGRATION_CONFIGURATION.md`
2. Review technical details: `REFACTORING_SUMMARY.md`
3. Open an issue on GitHub

## 📋 Checklist for Migration

- [ ] Read `MIGRATION_CONFIGURATION.md`
- [ ] Identify all environment variables currently in use
- [ ] Create/update `appsettings.json` or use env var override
- [ ] Test configuration in development
- [ ] Update deployment files (K8s manifests, docker-compose)
- [ ] Deploy and verify

## 🔗 Examples

- Docker Compose: `docker-compose.example.yml`
- Kubernetes: `kubernetes-example.yml`

---

**Last Updated:** 2026-01-31
**Status:** ✅ Complete
**Breaking Change:** Yes - Migration required
