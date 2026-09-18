namespace SlimData.ClusterFiles;

public sealed class FileTransferCapacityExceededException : Exception
{
    public FileTransferCapacityExceededException()
    {
    }

    public FileTransferCapacityExceededException(string message)
        : base(message)
    {
    }

    public FileTransferCapacityExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
