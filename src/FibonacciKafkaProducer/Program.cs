using Confluent.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddEnvironmentVariables();

var app = builder.Build();

app.MapGet("/", () => Results.Ok("FibonacciSender up. Use /send/{n} to send a message to Kafka."));

app.MapPost("/send/{n:int}", async (int n, IConfiguration config) =>
{
    var bootstrapServers = config["Kafka:BootstrapServers"] ?? config["Kafka__BootstrapServers"] ?? "kafka:9092";
    var topic = config["Kafka:Topic"] ?? "fibo-public";

    var producerConfig = new ProducerConfig
    {
        BootstrapServers = bootstrapServers
    };

    using var producer = new ProducerBuilder<Null, string>(producerConfig).Build();
    var value = n.ToString();

    var dr = await producer.ProduceAsync(topic, new Message<Null, string> { Value = value });
    return Results.Ok(new
    {
        Topic = topic,
        Partition = dr.Partition.Value,
        Offset = dr.Offset.Value,
        Value = value
    });
});

app.Run("http://0.0.0.0:8080");
