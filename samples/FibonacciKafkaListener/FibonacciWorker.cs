using Confluent.Kafka;

public sealed class FibonacciKafkaListener : BackgroundService
{
    private readonly ILogger<FibonacciKafkaListener> _logger;
    private readonly IConfiguration _configuration;

    public FibonacciKafkaListener(ILogger<FibonacciKafkaListener> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"] ?? _configuration["Kafka__BootstrapServers"] ?? "kafka:9092";
        var topic = _configuration["Kafka:Topic"] ?? _configuration["Kafka__Topic"] ?? "fibo-public";
        var groupId = _configuration["Kafka:GroupId"] ?? _configuration["Kafka__GroupId"] ?? "fibonacci-listener-group";

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<Null, string>(config).Build();
        consumer.Subscribe(topic);

        _logger.LogFibonacciKafkaListenerStartedTopicGroupId(topic, groupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var cr = consumer.Consume(stoppingToken);
                    if (cr is null) continue;

                    _logger.LogReceivedMessageFromKafkaTopicPartition(cr.Topic, cr.Partition.Value, cr.Offset.Value, cr.Message.Value);

                    if (int.TryParse(cr.Message.Value, out var n))
                    {
                        var fib = Fibonacci(n);
                        _logger.LogComputedFibonacci(n, fib);
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogKafkaConsumeError(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal on shutdown
        }
        finally
        {
            consumer.Close();
            _logger.LogFibonacciKafkaListenerStopped();
        }
    }

    private static long Fibonacci(int n)
    {
        if (n <= 0) return 0;
        if (n == 1) return 1;
        long a = 0, b = 1;
        for (var i = 2; i <= n; i++)
        {
            var tmp = a + b;
            a = b;
            b = tmp;
        }
        return b;
    }
}
