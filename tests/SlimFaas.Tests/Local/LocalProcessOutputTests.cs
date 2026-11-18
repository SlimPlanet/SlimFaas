using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalProcessOutputTests
{
    [Fact]
    public void Disk_errors_do_not_escape_process_callbacks_and_logging_recovers()
    {
        using var writer = new FailingWriter { Fail = true };
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        using var output = new LocalProcessOutput(writer, "function/test", "test.log", logger.Object);

        output.WriteLine("first");
        output.WriteLine("second");
        writer.Fail = false;
        output.WriteLine("recovered");

        Assert.Equal(["recovered"], writer.Lines);
        logger.Verify(value => value.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<IOException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(value => value.Log(LogLevel.Information, It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public void Disk_error_during_final_flush_does_not_abort_supervisor_cleanup()
    {
        var writer = new FailingWriter { FailOnDispose = true };
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var output = new LocalProcessOutput(writer, "node/test", "test.log", logger.Object);

        output.Dispose();

        Assert.True(writer.Disposed);
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        internal bool Fail { get; set; }
        internal bool FailOnDispose { get; set; }
        internal bool Disposed { get; private set; }
        internal List<string> Lines { get; } = [];
        public override void WriteLine(string? value)
        {
            if (Fail)
                throw new IOException("Disk full");
            Lines.Add(value!);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (FailOnDispose)
                throw new IOException("Flush failed");
            base.Dispose(disposing);
        }
    }
}
