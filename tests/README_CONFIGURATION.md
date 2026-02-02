# SlimFaas Tests - Configuration Update

## ⚠️ Important Changes

SlimFaas tests have been updated to reflect the new strongly-typed configuration system.

## Key Changes

### 1. Options Injection in Tests

All tests now inject `IOptions<SlimFaasOptions>` instead of using environment variables:

```csharp
// Old way (no longer used)
Environment.SetEnvironmentVariable("NAMESPACE", "test");

// New way
private static IOptions<SlimFaasOptions> CreateSlimFaasOptions()
{
    return Options.Create(new SlimFaasOptions
    {
        Namespace = "test",
        BaseFunctionPodUrl = "http://{pod_ip}:{pod_port}"
    });
}

// In test setup
services.AddSingleton(CreateSlimFaasOptions());
```

### 2. Test Files Updated

- **`EventEndpointsTests.cs`** - Updated to inject SlimFaasOptions
- **`SlimFaasOptionsTests.cs`** - New test file for options validation

### 3. Deprecated Tests

- **`EnvironmentVariablesTests.cs`** - Can be removed as EnvironmentVariables class is no longer used

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test file
dotnet test --filter FullyQualifiedName~SlimFaas.Tests.Options.SlimFaasOptionsTests

# Run tests with verbose output
dotnet test --logger "console;verbosity=detailed"
```

## Writing New Tests

When writing new tests that need configuration:

```csharp
using Microsoft.Extensions.Options;
using SlimFaas.Options;

public class MyTests
{
    private static IOptions<SlimFaasOptions> CreateOptions()
    {
        return Options.Create(new SlimFaasOptions
        {
            Namespace = "test-namespace",
            // ... other properties
        });
    }

    [Fact]
    public async Task MyTest()
    {
        // Arrange
        var options = CreateOptions();

        // Use options in service setup
        services.AddSingleton(options);

        // ...
    }
}
```

## Configuration in Tests

You can also use `ConfigurationBuilder` for more complex scenarios:

```csharp
var configurationBuilder = new ConfigurationBuilder();
configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["SlimFaas:Namespace"] = "test",
    ["Workers:DelayMilliseconds"] = "10"
});
var configuration = configurationBuilder.Build();

services.AddSlimFaasOptions(configuration);
```

## Migration Checklist

If you're updating an existing test:

- [ ] Remove any `Environment.SetEnvironmentVariable()` calls
- [ ] Add `using Microsoft.Extensions.Options;`
- [ ] Add `using SlimFaas.Options;`
- [ ] Create a helper method like `CreateSlimFaasOptions()`
- [ ] Inject options in service configuration: `services.AddSingleton(CreateSlimFaasOptions())`
- [ ] Update any service constructors that now require `IOptions<T>`

## See Also

- Root `/MIGRATION_CONFIGURATION.md` - Complete migration guide
- Root `/REFACTORING_SUMMARY.md` - Technical details
- `/tests/SlimFaas.Tests/Options/SlimFaasOptionsTests.cs` - Example tests
