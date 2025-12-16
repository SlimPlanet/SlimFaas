namespace ClusterFileDemoProdish.Cluster;

public static class MessageNames
{
    public const string MetaUpsert = "file.meta.upsert";
    public const string MetaDelete = "file.meta.delete";
    public const string MetaDumpRequest = "file.meta.dump";

    public const string FileGetRequest = "file.get";
    public const string FilePutPrefix = "file.put:"; // signal + stream, id in name
}
