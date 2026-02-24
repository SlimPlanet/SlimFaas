using System.Text.Json;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public class ScheduleJobBackupDataTests
{
    [Fact(DisplayName = "Sérialisation puis désérialisation roundtrip via le JsonContext AOT")]
    public void SerializeDeserialize_Roundtrip_Should_Preserve_Data()
    {
        // Arrange
        var original = new ScheduleJobBackupData
        {
            Hashsets = new()
            {
                ["ScheduleJob:fibonacci"] = new()
                {
                    ["id-1"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                    ["id-2"] = Convert.ToBase64String(new byte[] { 4, 5, 6 })
                },
                ["ScheduleJob:default"] = new()
                {
                    ["id-3"] = Convert.ToBase64String(new byte[] { 7, 8, 9 })
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(original, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);
        var deserialized = JsonSerializer.Deserialize(json, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Hashsets.Count);
        Assert.Equal(2, deserialized.Hashsets["ScheduleJob:fibonacci"].Count);
        Assert.Equal(1, deserialized.Hashsets["ScheduleJob:default"].Count);
        Assert.Equal(
            Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            deserialized.Hashsets["ScheduleJob:fibonacci"]["id-1"]);
    }

    [Fact(DisplayName = "Désérialisation d'un objet vide produit un Hashsets vide")]
    public void Deserialize_EmptyJson_Should_Produce_Empty_Hashsets()
    {
        // Arrange
        var json = """{"Hashsets":{}}""";

        // Act
        var deserialized = JsonSerializer.Deserialize(json, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized!.Hashsets);
    }

    [Fact(DisplayName = "Le constructeur par défaut initialise un Hashsets vide")]
    public void Default_Constructor_Should_Initialize_Empty_Hashsets()
    {
        // Act
        var data = new ScheduleJobBackupData();

        // Assert
        Assert.NotNull(data.Hashsets);
        Assert.Empty(data.Hashsets);
    }

    [Fact(DisplayName = "Le JSON généré est indenté (WriteIndented = true)")]
    public void Serialize_Should_Be_Indented()
    {
        // Arrange
        var data = new ScheduleJobBackupData
        {
            Hashsets = new()
            {
                ["ScheduleJob:test"] = new() { ["k"] = "dg==" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(data, ScheduleJobBackupDataJsonContext.Default.ScheduleJobBackupData);

        // Assert – indented JSON contains newlines
        Assert.Contains("\n", json);
    }
}

