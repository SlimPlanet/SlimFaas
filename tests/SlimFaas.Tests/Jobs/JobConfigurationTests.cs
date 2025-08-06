using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public class JobConfigurationTests
{
    [Fact]
    public void Constructeur_SansJson_DoitUtiliserValeursParDefaut()
    {
        // Arrange & Act
        JobConfiguration jobConfiguration = new();

        // Assert
        Assert.NotNull(jobConfiguration.Configuration);
        Assert.NotNull(jobConfiguration.Configuration.Configurations);

        // Vérifie que la clé "Default" existe
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("Default"));

        SlimfaasJob defaultJob = jobConfiguration.Configuration.Configurations["Default"];
        Assert.NotNull(defaultJob);
        Assert.NotNull(defaultJob.Resources);

        // Vérifie que les ressources par défaut sont bien "100m" et "100Mi"
        Assert.Equal("100m", defaultJob.Resources.Limits["cpu"]);
        Assert.Equal("100Mi", defaultJob.Resources.Limits["memory"]);
        Assert.Equal("100m", defaultJob.Resources.Requests["cpu"]);
        Assert.Equal("100Mi", defaultJob.Resources.Requests["memory"]);
    }

    [Fact]
    public void Constructeur_JsonInvalide_DoitCreerUneConfigurationParDefaut()
    {
        // Arrange
        // Un JSON invalide ou corrompu
        string jsonInvalide = "{ \"Configurations\": { \"MaFunction\": }"; // Manque des objets

        // Act
        JobConfiguration jobConfiguration = new(jsonInvalide);

        // Assert
        // Doit retomber sur la config par défaut
        Assert.NotNull(jobConfiguration.Configuration);
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("Default"));
    }

    [Fact]
    public void Constructeur_JsonValide_SansClefDefault_DoitAjouterUneConfigurationParDefaut()
    {
        // Arrange
        // Un JSON valide, mais sans la clé "Default"
        string jsonValideSansDefault = @"
            {
                ""Configurations"": {
                    ""MaFunction"": {
                        ""Name"": ""MaFunction"",
                        ""Something"": [""arg1"", ""arg2""],
                        ""Resources"": {
                            ""Limits"": { ""cpu"": ""200m"", ""memory"": ""256Mi"" },
                            ""Requests"": { ""cpu"": ""50m"",  ""memory"": ""64Mi"" }
                        }
                    }
                }
            }";

        // Act
        JobConfiguration jobConfiguration = new(jsonValideSansDefault);

        // Assert
        // On doit retrouver la clé "MaFunction" ET la clé "Default"
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("MaFunction"));
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("Default"));
    }

    [Fact]
    public void Constructeur_JsonValide_DoitParserLaConfigurationCorrectement()
    {
        // Arrange
        string jsonValide = @"
            {
                ""Configurations"": {
                    ""Default"": {
                        ""Name"": ""MaFonctionParDefaut"",
                        ""Something"": [""arg1"", ""arg2""],
                        ""Resources"": {
                            ""Limits"": { ""cpu"": ""500m"", ""memory"": ""512Mi"" },
                            ""Requests"": { ""cpu"": ""200m"",  ""memory"": ""256Mi"" }
                        }
                    },
                    ""AutreFunction"": {
                        ""Name"": ""AutreFunction"",
                        ""Something"": [],
                        ""Resources"": {
                            ""Limits"": { ""cpu"": ""1000m"", ""memory"": ""1Gi"" },
                            ""Requests"": { ""cpu"": ""500m"",  ""memory"": ""512Mi"" }
                        }
                    }
                }
            }";

        // Act
        JobConfiguration jobConfiguration = new(jsonValide);

        // Assert
        Assert.NotNull(jobConfiguration.Configuration);
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("Default"));
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("AutreFunction"));

        // Vérifie la configuration Default
        SlimfaasJob defaultJob = jobConfiguration.Configuration.Configurations["Default"];
        Assert.Equal("500m", defaultJob.Resources.Limits["cpu"]);
        Assert.Equal("512Mi", defaultJob.Resources.Limits["memory"]);
        Assert.Equal("200m", defaultJob.Resources.Requests["cpu"]);
        Assert.Equal("256Mi", defaultJob.Resources.Requests["memory"]);
    }

    [Fact]
    public void Constructeur_JsonValide_AvecClefDefaultEtRessourcesNull_DoitUtiliserLesRessourcesParDefaut()
    {
        // Arrange
        // Ici on fournit une config Default mais sans ressources
        string jsonAvecDefaultSansRessources = @"
            {
                ""Configurations"": {
                    ""Default"": {
                        ""Name"": ""MaFonctionSansRessources"",
                        ""Something"": []
                    }
                }
            }";

        // Act
        JobConfiguration jobConfiguration = new(jsonAvecDefaultSansRessources);

        // Assert
        // On vérifie que la clé "Default" existe et que les ressources sont remplies par défaut
        Assert.True(jobConfiguration.Configuration.Configurations.ContainsKey("Default"));
        SlimfaasJob defaultJob = jobConfiguration.Configuration.Configurations["Default"];
        Assert.NotNull(defaultJob.Resources);
        Assert.Equal("100m", defaultJob.Resources.Limits["cpu"]);
        Assert.Equal("100Mi", defaultJob.Resources.Limits["memory"]);
        Assert.Equal("100m", defaultJob.Resources.Requests["cpu"]);
        Assert.Equal("100Mi", defaultJob.Resources.Requests["memory"]);
    }
}
