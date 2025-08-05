using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public class JobResourceValidatorTests
{
    [Fact]
    public void ValidateResources_ShouldUseRequestedValues_WhenWithinLimits()
    {
        // Arrange
        CreateJobResources defaultConfig = new(
            new Dictionary<string, string> { { "cpu", "500m" }, { "memory", "1Gi" } },
            new Dictionary<string, string> { { "cpu", "1000m" }, { "memory", "2Gi" } }
        );

        CreateJobResources requestedConfig = new(
            new Dictionary<string, string> { { "cpu", "400m" }, { "memory", "512Mi" } },
            new Dictionary<string, string> { { "cpu", "800m" }, { "memory", "1.5Gi" } }
        );

        // Act
        CreateJobResources validatedConfig = JobResourceValidator.ValidateResources(defaultConfig, requestedConfig);

        // Assert
        Assert.Equal("400m", validatedConfig.Requests["cpu"]);
        Assert.Equal("512Mi", validatedConfig.Requests["memory"]);
        Assert.Equal("800m", validatedConfig.Limits["cpu"]);
        Assert.Equal("1.5Gi", validatedConfig.Limits["memory"]);
    }

    [Fact]
    public void ValidateResources_ShouldUseDefaultValues_WhenRequestedValuesExceedLimits()
    {
        // Arrange
        CreateJobResources defaultConfig = new(
            new Dictionary<string, string> { { "cpu", "500m" }, { "memory", "1Gi" } },
            new Dictionary<string, string> { { "cpu", "1000m" }, { "memory", "2Gi" } }
        );

        CreateJobResources requestedConfig = new(
            new Dictionary<string, string> { { "cpu", "600m" }, { "memory", "2Gi" } },
            new Dictionary<string, string> { { "cpu", "1500m" }, { "memory", "3Gi" } }
        );

        // Act
        CreateJobResources validatedConfig = JobResourceValidator.ValidateResources(defaultConfig, requestedConfig);

        // Assert
        Assert.Equal("500m", validatedConfig.Requests["cpu"]); // Dépassement -> valeur par défaut
        Assert.Equal("1Gi", validatedConfig.Requests["memory"]); // Respecté -> valeur demandée
        Assert.Equal("1000m", validatedConfig.Limits["cpu"]); // Dépassement -> valeur par défaut
        Assert.Equal("2Gi", validatedConfig.Limits["memory"]); // Dépassement -> valeur par défaut
    }

    [Fact]
    public void ValidateResources_ShouldUseDefaultValues_WhenKeyIsMissingInRequestedConfig()
    {
        // Arrange
        CreateJobResources defaultConfig = new(
            new Dictionary<string, string> { { "cpu", "500m" }, { "memory", "1Gi" } },
            new Dictionary<string, string> { { "cpu", "1000m" }, { "memory", "2Gi" } }
        );

        CreateJobResources requestedConfig = new(
            new Dictionary<string, string>(), // Aucune valeur demandée
            new Dictionary<string, string> { { "cpu", "800m" } } // Seul CPU est spécifié
        );

        // Act
        CreateJobResources validatedConfig = JobResourceValidator.ValidateResources(defaultConfig, requestedConfig);

        // Assert
        Assert.Equal("500m", validatedConfig.Requests["cpu"]); // Valeur par défaut car absente
        Assert.Equal("1Gi", validatedConfig.Requests["memory"]); // Valeur par défaut car absente
        Assert.Equal("800m", validatedConfig.Limits["cpu"]); // Valeur demandée car respectée
        Assert.Equal("2Gi", validatedConfig.Limits["memory"]); // Valeur par défaut car absente
    }

    [Fact]
    public void ValidateResources_ShouldHandleMixedUnitsCorrectly()
    {
        // Arrange
        CreateJobResources defaultConfig = new(
            new Dictionary<string, string> { { "cpu", "1000m" }, { "memory", "2Gi" } },
            new Dictionary<string, string> { { "cpu", "2000m" }, { "memory", "4Gi" } }
        );

        CreateJobResources requestedConfig = new(
            new Dictionary<string, string> { { "cpu", "1" }, { "memory", "2048Mi" } },
            new Dictionary<string, string> { { "cpu", "2500m" }, { "memory", "5Gi" } }
        );

        // Act
        CreateJobResources validatedConfig = JobResourceValidator.ValidateResources(defaultConfig, requestedConfig);

        // Assert
        Assert.Equal("1", validatedConfig.Requests["cpu"]); // 1 = 1000m, donc accepté
        Assert.Equal("2048Mi", validatedConfig.Requests["memory"]); // 2048Mi = 2Gi, accepté
        Assert.Equal("2000m", validatedConfig.Limits["cpu"]); // Dépassement -> valeur par défaut
        Assert.Equal("4Gi", validatedConfig.Limits["memory"]); // Dépassement -> valeur par défaut
    }
}
