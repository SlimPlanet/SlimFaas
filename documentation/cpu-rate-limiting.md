# CPU Rate Limiting

## Overview

SlimFaas includes a CPU-aware rate limiting mechanism to protect the system from overload during high traffic periods. This feature prevents CPU spikes that could cause the dotnet cluster to crash by implementing load shedding when CPU usage exceeds configured thresholds.

## Key Features

- **Native AOT Compatible**: Designed to work seamlessly with Native AOT compilation
- **Port-Specific Exclusions**: Applies to all ports except internal SlimData and configured ports
- **Hysteresis Support**: Prevents rapid toggling between limited and normal states
- **Configurable Response**: Customizable retry headers
- **Path Exclusions**: Optionally exclude health checks and metrics endpoints from rate limiting

## How It Works

The CPU rate limiting system monitors CPU usage at regular intervals and activates rate limiting when usage exceeds a high threshold. Once activated, it remains in effect until CPU usage drops below a low threshold (hysteresis), providing stable behavior during load fluctuations.

### Behavior

1. **Normal State**: When `CpuPercent < CpuHighThreshold`, all requests are processed normally
2. **Rate Limited State**: When `CpuPercent >= CpuHighThreshold`, new requests receive a 429 Too Many Requests error response
3. **Recovery**: Rate limiting only deactivates when `CpuPercent <= CpuLowThreshold`

### Port Filtering

The rate limiter applies to all HTTP traffic **except**:
- The internal SlimData port (used for Raft cluster communication)

This ensures internal cluster communication remains unaffected while protecting external-facing endpoints.

## Configuration

Add the `RateLimiting` section under `SlimFaas` in your `appsettings.json`:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": true,
      "CpuHighThreshold": 80.0,
      "CpuLowThreshold": 60.0,
      "SampleIntervalMs": 1000,
      "RetryAfterSeconds": 5,
      "ExcludedPaths": ["/health", "/ready", "/metrics"]
    }
  }
}
```

### Required Settings

| Setting | Type | Description |
|---------|------|-------------|
| `Enabled` | bool | Enable or disable CPU rate limiting |
| `CpuHighThreshold` | double | CPU percentage to activate rate limiting (0-100) |
| `CpuLowThreshold` | double | CPU percentage to deactivate rate limiting (0-100, must be < CpuHighThreshold) |
| `SampleIntervalMs` | int | How often to sample CPU usage in milliseconds (must be >= 100) |

### Optional Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `RetryAfterSeconds` | int | null | Seconds to wait before retrying (adds Retry-After header) |
| `ExcludedPaths` | string[] | [] | Paths to exclude from rate limiting (e.g., health checks) |

### Validation Rules

The configuration is validated at startup with the following rules:

- `CpuLowThreshold` must be between 0 and 100
- `CpuHighThreshold` must be between 0 and 100
- `CpuLowThreshold` must be less than `CpuHighThreshold`
- `SampleIntervalMs` must be at least 100

Invalid configuration will prevent the application from starting with a clear error message.

## Examples

### Basic Configuration

Minimal setup for production use:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": true,
      "CpuHighThreshold": 85.0,
      "CpuLowThreshold": 70.0,
      "SampleIntervalMs": 1000
    }
  }
}
```

### Advanced Configuration

With custom retry headers and path exclusions:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": true,
      "CpuHighThreshold": 80.0,
      "CpuLowThreshold": 60.0,
      "SampleIntervalMs": 500,
      "RetryAfterSeconds": 10,
      "ExcludedPaths": [
        "/health",
        "/ready",
        "/metrics",
        "/status"
      ]
    }
  }
}
```

### Development/Testing Configuration

Disabled for local development:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": false,
      "CpuHighThreshold": 90.0,
      "CpuLowThreshold": 70.0,
      "SampleIntervalMs": 1000
    }
  }
}
```

## Automatic Port Exclusions

SlimFaas automatically excludes the following ports from rate limiting:

- **SlimData Port**: The internal port used for Raft cluster communication

This automatic exclusion ensures that:
- Raft consensus operations continue uninterrupted
- Leader elections are not affected by high CPU
- State synchronization remains healthy
- Only external-facing traffic is subject to rate limiting

## Monitoring

When rate limiting is active, you'll see log messages indicating:

- When the rate limiter activates (CPU exceeds high threshold)
- When the rate limiter deactivates (CPU drops below low threshold)
- Which ports are excluded from rate limiting at startup
- Requests being rejected (429 responses)

Monitor these metrics to tune your thresholds appropriately for your workload.

## Best Practices

### Threshold Selection

- **High Threshold (80-90%)**: Set this to a level where your system is still responsive but approaching saturation
- **Low Threshold (60-70%)**: Set this 10-20% below the high threshold to prevent rapid toggling
- **Gap Between Thresholds**: A larger gap provides more stability but slower recovery

### Sample Interval

- **Fast Response (500-1000ms)**: Responds quickly to CPU spikes but may be more sensitive to brief fluctuations
- **Stable Response (2000-5000ms)**: More stable but slower to react to sudden load increases

### Path Exclusions

Always exclude:
- Health check endpoints used by load balancers or orchestrators (`/health`, `/ready`)
- Metrics endpoints used by monitoring systems (`/metrics`)
- Any critical internal endpoints

### Port Architecture

The automatic exclusion of the SlimData port ensures your control plane remains stable:
- Public-facing function endpoints can be rate limited
- Internal cluster communication is never throttled
- You can add additional port exclusions if needed for specific use cases

## Troubleshooting

### Rate Limiting Not Activating

1. Verify `Enabled` is set to `true`
2. Check that requests are coming to non-excluded ports
3. Verify CPU is actually reaching the `CpuHighThreshold`
4. Check logs for any configuration validation errors
5. Confirm the request path is not in `ExcludedPaths`

### Rate Limiting Too Aggressive

1. Increase the `CpuHighThreshold`
2. Increase the `SampleIntervalMs` for more stability
3. Add more paths to `ExcludedPaths` if needed
4. Consider horizontal scaling to distribute load

### Rate Limiting Not Deactivating

1. Check if CPU is dropping below `CpuLowThreshold`
2. Verify the gap between high and low thresholds is appropriate
3. Look for background processes keeping CPU elevated

### Internal Communication Issues

If you experience issues with cluster communication:
1. Check logs to confirm the SlimData port is being excluded
2. Verify the `publicEndPoint` configuration is correct
3. Ensure no firewall or network policies are blocking internal traffic

## Technical Details

### Implementation

The CPU rate limiting feature consists of:

- **CpuMonitoringService**: Background service that samples CPU usage at regular intervals
- **ICpuUsageProvider**: Interface providing cached CPU percentage values
- **CpuRateLimitingMiddleware**: Middleware that checks CPU usage and rejects requests when threshold is exceeded
- **RateLimitingOptions**: Strongly-typed configuration class with validation
- **Automatic Port Detection**: Extracts the SlimData port from the cluster configuration

### Native AOT Compatibility

The implementation is fully compatible with Native AOT compilation:
- Uses source-generated JSON serialization
- Avoids reflection-based approaches
- Leverages the options pattern with AOT-safe validation

### Performance Impact

The CPU monitoring service runs on a background thread and caches the current CPU percentage. Checking whether to rate limit a request involves:
1. Reading a cached double value (CPU percentage)
2. A simple numeric comparison
3. Checking if the port is in the excluded list (array contains check)
4. Checking if the path is excluded (optional)

This minimal overhead ensures the rate limiter itself doesn't contribute significantly to CPU usage.

## Related Documentation

- [How It Works](how-it-works.md) - Detailed architecture and CPU rate limiting flow
- [Autoscaling](autoscaling.md) - Learn about horizontal scaling options
- [OpenTelemetry](opentelemetry.md) - Monitor CPU and performance metrics
- [Getting Started](get-started.md) - Initial setup and configuration
