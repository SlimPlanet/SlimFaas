using ClusterFileDemoProdish.Models;
using ClusterFileDemoProdish.Storage;
using DotNext.Net.Cluster.Messaging;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using DotNext.IO;

namespace ClusterFileDemoProdish.Cluster;

/// <summary>
/// Receives file-meta signals, file put broadcasts, and file-get/meta-dump requests.
/// </summary>
public sealed class ClusterMessagingChannel : IInputChannel
{
    private readonly IKvStore _kv;
    private readonly IFileRepository _files;
    private readonly ILogger<ClusterMessagingChannel> _logger;

    public ClusterMessagingChannel(IKvStore kv, IFileRepository files, ILogger<ClusterMessagingChannel> logger)
    {
        _kv = kv;
        _files = files;
        _logger = logger;
    }

    public bool IsSupported(string messageName, bool oneWay)
    {
        if (oneWay)
        {
            if (messageName == MessageNames.MetaUpsert) return true;
            if (messageName == MessageNames.MetaDelete) return true;
            if (messageName.StartsWith(MessageNames.FilePutPrefix, StringComparison.Ordinal)) return true;
            return false;
        }

        return messageName == MessageNames.FileGetRequest || messageName == MessageNames.MetaDumpRequest;
    }

    public async Task ReceiveSignal(ISubscriber sender, IMessage signal, object? context, CancellationToken token)
    {
        try
        {
            if (signal.Name == MessageNames.MetaUpsert)
            {
                var json = await signal.ReadAsTextAsync(token);
                var meta = JsonSerializer.Deserialize<FileMeta>(json);
                if (meta is null) return;

                await _kv.SetAsync(Keys.Meta(meta.Id), Encoding.UTF8.GetBytes(json), timeToLiveMilliseconds: null);
                return;
            }

            if (signal.Name == MessageNames.MetaDelete)
            {
                var id = (await signal.ReadAsTextAsync(token)).Trim();
                if (string.IsNullOrWhiteSpace(id)) return;

                await _kv.DeleteAsync(Keys.Meta(id));
                await _files.DeleteAsync(id, token);
                return;
            }

            if (signal.Name.StartsWith(MessageNames.FilePutPrefix, StringComparison.Ordinal))
            {
                var id = signal.Name.Substring(MessageNames.FilePutPrefix.Length);
                if (string.IsNullOrWhiteSpace(id)) return;

                if (signal is not IDataTransferObject dto)
                {
                    _logger.LogWarning("file.put received with non-data payload");
                    return;
                }

                await _files.WriteFromDataTransferObjectAsync(id, dto, chunkSize: 64 * 1024, token);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReceiveSignal failed for {Name} from {Sender}", signal.Name, sender);
        }
        finally
        {
            (signal as IDisposableMessage)?.Dispose();
        }
    }

    public async Task<IMessage> ReceiveMessage(ISubscriber sender, IMessage message, object? context, CancellationToken token)
    {
        try
        {
            if (message.Name == MessageNames.FileGetRequest)
            {
                var id = (await message.ReadAsTextAsync(token)).Trim();

                if (string.IsNullOrWhiteSpace(id))
                    return new TextMessage("BAD_REQUEST", "file.get.error");

                var (exists, path) = await _files.TryGetAsync(id, token);
                if (!exists)
                    return new TextMessage("NOT_FOUND", "file.get.error");

                var meta = await TryGetMetaAsync(id, token);
                var ct = meta?.ContentType ?? MediaTypeNames.Application.Octet;

                var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

                return new StreamMessage(stream, false,"file.get.ok", new System.Net.Mime.ContentType(ct));
            }

            if (message.Name == MessageNames.MetaDumpRequest)
            {
                if (_kv is not SlimDataKvStore slim)
                    return new TextMessage("[]", "file.meta.dump.ok");

                var snapshot = slim.Snapshot();
                var metas = new List<FileMeta>();

                foreach (var (key, entry) in snapshot)
                {
                    if (!key.StartsWith(Keys.MetaPrefix, StringComparison.Ordinal)) continue;

                    try
                    {
                        var json = Encoding.UTF8.GetString(entry.Value);
                        var meta = JsonSerializer.Deserialize<FileMeta>(json);
                        if (meta is not null && !meta.IsExpired)
                            metas.Add(meta);
                    }
                    catch
                    {
                        // ignore malformed entries
                    }
                }

                var payload = JsonSerializer.Serialize(metas);
                return new TextMessage(payload, "file.meta.dump.ok");
            }

            return new TextMessage("NOT_SUPPORTED", "error");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReceiveMessage failed for {Name} from {Sender}", message.Name, sender);
            return new TextMessage("ERROR", "error");
        }
        finally
        {
            (message as IDisposableMessage)?.Dispose();
        }
    }

    private async Task<FileMeta?> TryGetMetaAsync(string id, CancellationToken ct)
    {
        var bytes = await _kv.GetAsync(Keys.Meta(id));
        if (bytes is null) return null;

        try
        {
            return JsonSerializer.Deserialize<FileMeta>(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static class Keys
    {
        public const string MetaPrefix = "filemeta:";
        public static string Meta(string id) => $"{MetaPrefix}{id}";
    }
}
