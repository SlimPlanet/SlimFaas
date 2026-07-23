namespace SlimData.ClusterFiles;

public sealed class FileTransferCapacityExceededException : Exception
{
    public FileTransferCapacityExceededException(string message)
        : base(message)
    {
    }
}
