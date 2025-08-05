using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;


public record CreateScheduleJobResult(string ErrorKey, string ElementId, int Code = 400);

public class ScheduleJobService(IJobConfiguration jobConfiguration, IDatabaseService databaseService)
{
    async Task<CreateScheduleJobResult> CreateScheduleJob(string name, ScheduleCreateJob createJob, bool isMessageComeFromNamespaceInternal)
    {
        var configuration = jobConfiguration.Configuration.Configurations;
        name = configuration.ContainsKey(name) ? name : JobService.Default;
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return new CreateScheduleJobResult("Visibility_private", string.Empty, 400);
        }

        if (createJob.Image != string.Empty && !JobService.IsImageAllowed(conf.ImagesWhitelist, createJob.Image))
        {
            return new CreateScheduleJobResult("Image_not_allowed", string.Empty);
        }

        var idSchedule = Guid.NewGuid().ToString();
        byte[] memory = MemoryPackSerializer.Serialize(createJob);
        var dictionary = new Dictionary<string, byte[]>();
        dictionary.Add(idSchedule, memory);
        await databaseService.HashSetAsync(name.ToLower(), dictionary);
        return new CreateScheduleJobResult(string.Empty, idSchedule, 200);
    }

}
