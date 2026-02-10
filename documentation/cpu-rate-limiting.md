# CPU Rate Limiting

## Overview

SlimFaas includes a CPU-aware rate limiting mechanism to protect the system from overload during high traffic periods. This feature prevents CPU spikes that could cause the dotnet cluster to crash by implementing load shedding when CPU usage exceeds configured thresholds.

## Key Features

- **Native AOT Compatible**: Designed to work seamlessly with Native AOT compilation
- **Port-Specific**: Only applies to public HTTP traffic, never affects internal cluster communication
- **Hysteresis Support**: Prevents rapid toggling between limited and normal states
- **Configurable Response**: Customizable HTTP status codes and retry headers
- **Path Exclusions**: Optionally exclude health checks and metrics endpoints from rate limiting

## How It Works

The CPU rate limiting system monitors CPU usage at regular intervals and activates rate limiting when usage exceeds a high threshold. Once activated, it remains in effect until CPU usage drops below a low threshold (hysteresis), providing stable behavior during load fluctuations.

### Behavior

1. **Normal State**: When `CpuPercent < CpuHighThreshold`, all requests are processed normally
2. **Rate Limited State**: When `CpuPercent >= CpuHighThreshold`, new requests receive a configured error response (default: 429 Too Many Requests)
3. **Recovery**: Rate limiting only deactivates when `CpuPercent <= CpuLowThreshold`

### Port Filtering

The rate limiter only applies to requests on the configured public port. Requests on other ports, including the internal cluster port, are **never** rate limited, ensuring internal communication remains unaffected.

## Configuration

Add the `RateLimiting` section under `SlimFaas` in your `appsettings.json`:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": true,
      "PublicPort": 5000,
      "CpuHighThreshold": 80.0,
      "CpuLowThreshold": 60.0,
      "SampleIntervalMs": 1000,
      "StatusCode": 429,
      "RetryAfterSeconds": 5,
      "ExcludedPaths": ["/health", "/metrics"]
    }
  }
}
```

### Required Settings

| Setting | Type | Description |
|---------|------|-------------|
| `Enabled` | bool | Enable or disable CPU rate limiting |
| `PublicPort` | int | The public port to apply rate limiting (must be > 0) |
| `CpuHighThreshold` | double | CPU percentage to activate rate limiting (0-100) |
| `CpuLowThreshold` | double | CPU percentage to deactivate rate limiting (0-100, must be < CpuHighThreshold) |
| `SampleIntervalMs` | int | How often to sample CPU usage in milliseconds (must be >= 100) |

### Optional Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `StatusCode` | int | 429 | HTTP status code to return when rate limited |
| `RetryAfterSeconds` | int | null | Seconds to wait before retrying (adds Retry-After header) |
| `ExcludedPaths` | string[] | [] | Paths to exclude from rate limiting (e.g., health checks) |

### Validation Rules

The configuration is validated at startup with the following rules:

- `PublicPort` must be greater than 0
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
      "PublicPort": 5000,
      "CpuHighThreshold": 85.0,
      "CpuLowThreshold": 70.0,
      "SampleIntervalMs": 1000
    }
  }
}
```

### Advanced Configuration

With custom response codes and path exclusions:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": true,
      "PublicPort": 5000,
      "CpuHighThreshold": 80.0,
      "CpuLowThreshold": 60.0,
      "SampleIntervalMs": 500,
      "StatusCode": 503,
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
      "PublicPort": 5000,
      "CpuHighThreshold": 90.0,
      "CpuLowThreshold": 70.0,
      "SampleIntervalMs": 1000
    }
  }
}
```

## Monitoring

When rate limiting is active, you'll see log messages indicating:

- When the rate limiter activates (CPU exceeds high threshold)
- When the rate limiter deactivates (CPU drops below low threshold)
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
- Health check endpoints used by load balancers or orchestrators
- Metrics endpoints used by monitoring systems
- Any critical internal endpoints

### Port Configuration

Ensure the `PublicPort` matches your actual public-facing port. Internal cluster communication ports should never be configured here.

## Troubleshooting

### Rate Limiting Not Activating

1. Verify `Enabled` is set to `true`
2. Check that requests are coming to the configured `PublicPort`
3. Verify CPU is actually reaching the `CpuHighThreshold`
4. Check logs for any configuration validation errors

### Rate Limiting Too Aggressive

1. Increase the `CpuHighThreshold`
2. Increase the `SampleIntervalMs` for more stability
3. Add more paths to `ExcludedPaths` if needed
4. Consider horizontal scaling to distribute load

### Rate Limiting Not Deactivating

1. Check if CPU is dropping below `CpuLowThreshold`
2. Verify the gap between high and low thresholds is appropriate
3. Look for background processes keeping CPU elevated

## Technical Details

### Implementation

The CPU rate limiting feature consists of:

- **CpuMonitoringService**: Background service that samples CPU usage at regular intervals
- **ICpuUsageProvider**: Interface providing cached CPU percentage values
- **CpuRateLimitingMiddleware**: Middleware that checks CPU usage and rejects requests when threshold is exceeded
- **RateLimitingOptions**: Strongly-typed configuration class with validation

### Native AOT Compatibility

The implementation is fully compatible with Native AOT compilation:
- Uses source-generated JSON serialization
- Avoids reflection-based approaches
- Leverages the options pattern with AOT-safe validation

### Performance Impact

The CPU monitoring service runs on a background thread and caches the current CPU percentage. Checking whether to rate limit a request involves:
1. Reading a cached double value (CPU percentage)
2. A simple numeric comparison
3. Checking if the port matches (optional)
4. Checking if the path is excluded (optional)

This minimal overhead ensures the rate limiter itself doesn't contribute significantly to CPU usage.

## Related Documentation

- [Autoscaling](autoscaling.md) - Learn about horizontal scaling options
- [OpenTelemetry](opentelemetry.md) - Monitor CPU and performance metrics
- [Getting Started](get-started.md) - Initial setup and configuration

