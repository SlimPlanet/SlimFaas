using System.Buffers;
using System.Net;
using MemoryPack;
using SlimData;
using SlimData.Commands;
using SlimFaas.Options;

namespace SlimFaas.Tests.Database;

[CollectionDefinition("MemoryPack client registration", DisableParallelization = true)]
public sealed class MemoryPackClientRegistrationCollection;

[Collection("MemoryPack client registration")]
public sealed partial class SlimDataRecoveryTests
{
    [Theory]
    [InlineData("admission-result", SlimDataBatchMode.Global)]
    [InlineData("admission-result", SlimDataBatchMode.PartitionedByKey)]
    [InlineData("admission-mutation", SlimDataBatchMode.Global)]
    [InlineData("admission-mutation", SlimDataBatchMode.PartitionedByKey)]
    [InlineData("request", SlimDataBatchMode.Global)]
    [InlineData("request", SlimDataBatchMode.PartitionedByKey)]
    [InlineData("response", SlimDataBatchMode.Global)]
    [InlineData("response", SlimDataBatchMode.PartitionedByKey)]
    [InlineData("response-conflict", SlimDataBatchMode.Global)]
    [InlineData("response-conflict", SlimDataBatchMode.PartitionedByKey)]
    [InlineData("stream", SlimDataBatchMode.Global)]
    [InlineData("stream", SlimDataBatchMode.PartitionedByKey)]
    [InlineData("dead-letter", SlimDataBatchMode.Global)]
    [InlineData("dead-letter", SlimDataBatchMode.PartitionedByKey)]
    public async Task Root_formatter_registration_window_does_not_fail_a_valid_mutation(
        string target, SlimDataBatchMode mode)
    {
        byte[] admissionResult = MemoryPackSerializer.Serialize(new FileAdmissionResult
        {
            Applied = true, Phase = FileAdmissionPhase.Pending
        });
        SlimDataBatchOperation? received = null;
        using var handler = new Handler(async (request, token) =>
        {
            byte[] payload = await request.Content!.ReadAsByteArrayAsync(token);
            // The fake peer uses generated dispatch so injecting the client's
            // pending root cache cannot make the fake server fail instead.
            var batch = ReadRoot<SlimDataCommandBatchRequest>(payload);
            received = Assert.Single(batch.Operations);
            var response = new SlimDataCommandBatchResponse
            {
                ProducerId = batch.ProducerId, Sequence = batch.Sequence, RequestId = batch.RequestId,
                Duplicate = target == "response-conflict",
                Results = [new()
                {
                    Kind = received.Kind, RequestId = received.RequestId, Applied = true,
                    KeyValueResult = new() { Status = KeyValueCommandStatus.Applied, Value = admissionResult },
                    StreamResult = new() { Status = 200 }, DeadLetterResult = new() { Status = 200 }
                }]
            };
            return new HttpResponseMessage(target == "response-conflict" ? HttpStatusCode.Conflict : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(WriteRoot(response))
            };
        });
        await using var fixture = await Fixture.CreateAsync(handler, mode: mode);
        Action restore = target switch
        {
            "admission-result" => SuspendRootFormatter<FileAdmissionResult>(),
            "admission-mutation" => SuspendRootFormatter<FileAdmissionMutation>(),
            "request" => SuspendRootFormatter<SlimDataCommandBatchRequest>(),
            "response" or "response-conflict" => SuspendRootFormatter<SlimDataCommandBatchResponse>(),
            "stream" => SuspendRootFormatter<DurableStreamMutation>(),
            "dead-letter" => SuspendRootFormatter<DeadLetterMutation>(),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        try
        {
            if (target == "stream")
                Assert.Equal(200, (await fixture.Service.ExecuteStreamAsync(new() { Stream = "orders" }).WaitAsync(TestDeadline)).Status);
            else if (target == "dead-letter")
                Assert.Equal(200, (await fixture.Service.ExecuteDeadLetterAsync(new() { Function = "billing" }).WaitAsync(TestDeadline)).Status);
            else
            {
                var result = await fixture.Service.ExecuteFileAdmissionAsync(new()
                {
                    Action = FileAdmissionAction.Begin, FileId = "physical", Token = Guid.NewGuid().ToString("N"),
                    DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(2).Ticks
                }).WaitAsync(TestDeadline);
                Assert.True(result.Applied);
                Assert.Equal(FileAdmissionPhase.Pending, result.Phase);
                Assert.Null(result.Error);
            }
            Assert.NotNull(received);
        }
        finally
        {
            restore();
        }
    }

    private static byte[] WriteRoot<T>(T value) where T : IMemoryPackable<T>
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var state = MemoryPackWriterOptionalStatePool.Rent(null);
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, state);
        writer.WritePackable(value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static T ReadRoot<T>(ReadOnlySpan<byte> payload) where T : IMemoryPackable<T>
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        using var reader = new MemoryPackReader(payload, state);
        return reader.ReadPackable<T>()!;
    }

    private static Action SuspendRootFormatter<T>() where T : IMemoryPackFormatterRegister
    {
        MemoryPackFormatterProvider.Register<T>();
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        using var reader = new MemoryPackReader(ReadOnlySpan<byte>.Empty, state);
        var formatter = (MemoryPackFormatter<T>)reader.GetFormatter<T>();
        // Deterministically model registration's published flag before its
        // formatter assignment, without timing sleeps or a probabilistic race.
        MemoryPackFormatterProvider.Register<T>(null!);
        return () => MemoryPackFormatterProvider.Register(formatter);
    }
}
