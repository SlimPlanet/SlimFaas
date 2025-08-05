using SlimFaas.Jobs;

namespace SlimFaas.Tests.Jobs;
using System;
using Xunit;

public class CronUtilsTests
{
    [Theory]
    // Cron,              "Current Timestamp",          "Expected Last Execution"
    [InlineData("0 0 * * *",    "2024-06-09T12:34:56Z",  "2024-06-09T00:00:00Z")] // chaque jour à minuit
    [InlineData("0 12 * * *",   "2024-06-09T12:00:00Z",  "2024-06-09T12:00:00Z")] // pile sur exécution
    [InlineData("0 12 * * *",   "2024-06-09T13:00:00Z",  "2024-06-09T12:00:00Z")] // juste après exécution
    [InlineData("0 12 * * *",   "2024-06-09T11:59:00Z",  "2024-06-08T12:00:00Z")] // juste avant exécution

    [InlineData("*/15 * * * *", "2024-06-09T12:45:00Z",  "2024-06-09T12:45:00Z")] // toutes les 15 min, sur
    [InlineData("*/15 * * * *", "2024-06-09T12:46:00Z",  "2024-06-09T12:45:00Z")] // toutes les 15 min, après
    [InlineData("*/15 * * * *", "2024-06-09T12:14:00Z",  "2024-06-09T12:00:00Z")] // toutes les 15 min, avant

    [InlineData("0 0 1 * *",    "2024-06-09T10:00:00Z",  "2024-06-01T00:00:00Z")] // 1er du mois
    [InlineData("0 0 1 * *",    "2024-01-01T00:00:00Z",  "2024-01-01T00:00:00Z")] // 1er, pile
    [InlineData("0 0 1 * *",    "2024-01-01T00:15:00Z",  "2024-01-01T00:00:00Z")]

    [InlineData("0 0 * * 0",    "2024-06-09T12:00:00Z",  "2024-06-09T00:00:00Z")] // chaque dimanche minuit
    [InlineData("0 0 * * 0",    "2024-06-10T00:00:00Z",  "2024-06-09T00:00:00Z")] // lundi, dernière exécution dimanche

    // Plages et listes
    [InlineData("0 9-17 * * 1-5", "2024-06-10T12:00:00Z", "2024-06-10T12:00:00Z")] // jour ouvré, heure ouvrée
    [InlineData("0 9-17 * * 1-5", "2024-06-10T18:00:00Z", "2024-06-10T17:00:00Z")] // après dernier créneau du jour
    [InlineData("0 9,17 * * 1,5", "2024-06-07T17:01:00Z", "2024-06-07T17:00:00Z")] // vendredi 17h, sur et après

    // Pas
    [InlineData("0 */3 * * *",  "2024-06-09T12:00:00Z",  "2024-06-09T12:00:00Z")] // toutes les 3h, pile
    [InlineData("0 */3 * * *",  "2024-06-09T13:00:00Z",  "2024-06-09T12:00:00Z")] // toutes les 3h, après
    [InlineData("0 */3 * * *",  "2024-06-09T00:01:00Z",  "2024-06-09T00:00:00Z")] // juste après minuit

    // Passage de mois/année
    [InlineData("0 0 * 1 *",    "2024-01-01T00:00:00Z",  "2024-01-01T00:00:00Z")] // 1er janvier minuit
    [InlineData("0 0 * 1 *",    "2024-01-31T23:59:59Z",  "2024-01-31T00:00:00Z")] // dernier jour janvier
    [InlineData("0 0 31 12 *",  "2023-12-31T23:59:59Z",  "2023-12-31T00:00:00Z")] // 31 décembre

    // Duplicité dom/dow
    [InlineData("0 12 10 * 5",  "2024-05-10T12:00:00Z",  "2024-05-10T12:00:00Z")] // 10 du mois OU vendredi
    [InlineData("0 12 10 * 5",  "2024-05-17T12:00:00Z",  "2024-05-17T12:00:00Z")] // vendredi (autre que le 10)
    [InlineData("0 12 10 * 5",  "2024-05-18T12:00:00Z",  "2024-05-17T12:00:00Z")] // samedi (le 10/05 était vendredi)

    // Cas limites (changement d'heure)
    [InlineData("0 * * * *",    "2024-03-31T01:59:00Z",  "2024-03-31T01:00:00Z")] // minute pile avant passage heure d'été Europe
    [InlineData("0 * * * *",    "2024-10-27T01:59:00Z",  "2024-10-27T01:00:00Z")] // minute pile avant passage heure d'hiver Europe

    public void GetLatestJobExecutionTimestamp_ValidCases(string cron, string currentUtc, string expectedUtc)
    {
        var currentTs = DateTimeOffset.Parse(currentUtc).ToUnixTimeSeconds();
        var expectedTs = DateTimeOffset.Parse(expectedUtc).ToUnixTimeSeconds();

        var result = Cron.GetLatestJobExecutionTimestamp(cron, currentTs);

        Assert.Equal(expectedTs, result);
    }

    [Theory]
    [InlineData("0 25 * * *")]          // heure invalide
    [InlineData("0 0 32 * *")]          // jour du mois invalide
    [InlineData("61 * * * *")]          // minute invalide
    [InlineData("0 0 * 13 *")]          // mois invalide
    [InlineData("invalid cron")]        // pas assez de champs
    [InlineData("")]                    // vide
    public void GetLatestJobExecutionTimestamp_InvalidCron_Throws(string cron)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.ThrowsAny<Exception>(() => Cron.GetLatestJobExecutionTimestamp(cron, ts));
    }
}

