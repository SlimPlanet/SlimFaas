﻿/*using System.Collections.Immutable;
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
    public string IdTransaction { get; set; } = "";
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

    public int NumberAvailable { get; set; }
    public int NumberRunning { get; set; }
    public int NumberWaitingForRetry { get; set; }
    public int NumberFinished { get; set; }

    public int Total {get;set;}


    public List<TempQueueElement> Queues { get; set; } = new List<TempQueueElement>();


    public static SlimFaasQueuesData MapToNewModel(ImmutableList<QueueElement> data)
    {
        var result = new SlimFaasQueuesData
        {
            Queues =  new List<TempQueueElement>()
        };
        var ticks = DateTime.UtcNow.Ticks;
        result.NumberAvailable = data.GetQueueAvailableElement(ticks, 10000).Count;
        result.NumberRunning = data.GetQueueRunningElement(ticks).Count;
        result.NumberWaitingForRetry = data.GetQueueWaitingForRetryElement(ticks).Count;
        result.NumberFinished = data.GetQueueFinishedElement(ticks).Count;

        result.Total = result.NumberAvailable + result.NumberRunning + result.NumberWaitingForRetry +
                       result.NumberFinished;
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
                        HttpCode = rqe.HttpCode,
                        IdTransaction = rqe.IdTransaction
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
*/
