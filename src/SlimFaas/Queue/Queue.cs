using System.Text.Json.Serialization;
using SlimData;

namespace SlimFaas.Queue;

public record TempQueueElementRetryQueueElement
{
    public long StartTimeStamp { get; set; }
    public double StartTimeSpan { get; set; }
    public long EndTimeStamp { get; set; }
    public double EndTimeSpan { get; set; }
    public int HttpCode { get; set; }
}

public record TempQueueElement
{
    public string Id { get; set; } = "";
    public long InsertTimeStamp { get; set; }
    public double InsertTimeSpan { get; set; }
    public int HttpTimeout { get; set; }

    public List<TempQueueElementRetryQueueElement> RetryQueueElements { get; set; } =
        new List<TempQueueElementRetryQueueElement>();
}

public record SlimFaasQueuesData
{
    public List<TempQueueElement> Queues { get; set; } = new List<TempQueueElement>();

    public int NumberAvailable { get; set; }
    public int NumberRunning { get; set; }
    public int NumberWaitingForRetry { get; set; }

    public static SlimFaasQueuesData MapToNewModel(List<QueueElement> data)
    {
        var result = new SlimFaasQueuesData
        {
            Queues =  new List<TempQueueElement>()
        };
        var ticks = DateTime.UtcNow.Ticks;
        result.NumberAvailable = data.GetQueueRunningElement(ticks).Count;
        result.NumberRunning = data.GetQueueRunningElement(ticks).Count;
        result.NumberWaitingForRetry = data.GetQueueRunningElement(ticks).Count;
        var newQueueList = new List<TempQueueElement>();
        foreach (var kvp in data.OrderBy(k => k.InsertTimeStamp))
        {
                var newQueueElement = new TempQueueElement
                {
                    Id = kvp.Id,
                    InsertTimeStamp = kvp.InsertTimeStamp,
                    InsertTimeSpan = TimeSpan.FromTicks(DateTime.UtcNow.Ticks -kvp.InsertTimeStamp).TotalSeconds,
                    HttpTimeout = kvp.HttpTimeout,
                    RetryQueueElements = kvp.RetryQueueElements.Select(rqe => new TempQueueElementRetryQueueElement
                    {
                        StartTimeStamp = rqe.StartTimeStamp,
                        StartTimeSpan = TimeSpan.FromTicks(DateTime.UtcNow.Ticks -rqe.StartTimeStamp).TotalSeconds,
                        EndTimeStamp = rqe.EndTimeStamp,
                        EndTimeSpan =  rqe.EndTimeStamp == 0 ? 0 : TimeSpan.FromTicks(DateTime.UtcNow.Ticks -rqe.EndTimeStamp).TotalSeconds,
                        HttpCode = rqe.HttpCode
                    }).ToList()
                };
                newQueueList.Add(newQueueElement);

        }
        result.Queues = newQueueList;
        return result;
    }
}

[JsonSerializable(typeof(SlimFaasQueuesData))]
[JsonSerializable(typeof(TempQueueElement))]
[JsonSerializable(typeof(TempQueueElementRetryQueueElement))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SlimFaasQueuesDataSerializerContext : JsonSerializerContext;
