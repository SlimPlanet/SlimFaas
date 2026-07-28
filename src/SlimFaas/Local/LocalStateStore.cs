using System.Text.Json;

namespace SlimFaas.Local;

/// <summary>
/// Owns the on-disk marker used by local mode and the stable replica-to-port
/// assignments. The marker deliberately lives inside the directory it protects,
/// so --clean can never remove an arbitrary unowned directory.
/// </summary>
public sealed class LocalStateStore : IDisposable
{
    public const string MarkerFileName = ".slimfaas-local.json";

    private readonly LoadedLocalManifest _loaded;
    private readonly object _gate = new();
    private LocalStateMetadata _metadata;

    private LocalStateStore(LoadedLocalManifest loaded, LocalStateMetadata metadata)
    {
        _loaded = loaded;
        _metadata = metadata;
    }

    public string DirectoryPath => _loaded.StateDirectory;
    public string LogsDirectory => Path.Combine(DirectoryPath, "logs");
    public string DataDirectory => Path.Combine(DirectoryPath, "data");

    public static LocalStateStore Open(LoadedLocalManifest loaded, bool clean)
    {
        string directory = loaded.StateDirectory;
        string marker = Path.Combine(directory, MarkerFileName);

        if (clean && Directory.Exists(directory))
        {
            if (!File.Exists(marker))
            {
                throw new LocalManifestException(
                    $"Refusing --clean for '{directory}': the SlimFaas local ownership marker is missing.");
            }

            LocalStateMetadata existing = ReadMarker(marker);
            if (!string.Equals(existing.Project, loaded.Manifest.Name, StringComparison.Ordinal))
            {
                throw new LocalManifestException(
                    $"Refusing --clean for '{directory}': it belongs to project '{existing.Project}'.");
            }

            Directory.Delete(directory, recursive: true);
        }

        LocalStateMetadata metadata;
        if (File.Exists(marker))
        {
            metadata = ReadMarker(marker);
            if (!string.Equals(metadata.Project, loaded.Manifest.Name, StringComparison.Ordinal))
            {
                throw new LocalManifestException(
                    $"State directory '{directory}' belongs to project '{metadata.Project}', not '{loaded.Manifest.Name}'.");
            }

            if (metadata.Nodes != loaded.Manifest.Cluster.Nodes)
            {
                throw new LocalManifestException(
                    $"State directory was created for {metadata.Nodes} nodes, but the manifest requests " +
                    $"{loaded.Manifest.Cluster.Nodes}. Run local up with --clean.");
            }
        }
        else
        {
            metadata = new LocalStateMetadata(
                SchemaVersion: 1,
                Project: loaded.Manifest.Name,
                Nodes: loaded.Manifest.Cluster.Nodes,
                GatewayPort: loaded.Manifest.Cluster.GatewayPort,
                HttpPortBase: loaded.Manifest.Cluster.HttpPortBase,
                RaftPortBase: loaded.Manifest.Cluster.RaftPortBase,
                ProcessPortFrom: loaded.Manifest.ProcessPorts.From,
                ProcessPortTo: loaded.Manifest.ProcessPorts.To,
                PortAllocations: new Dictionary<string, int>(StringComparer.Ordinal));
        }

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        Directory.CreateDirectory(Path.Combine(directory, "data"));

        var result = new LocalStateStore(loaded, metadata);
        result.Save();
        return result;
    }

    public int? GetAllocatedPort(string replicaId)
    {
        lock (_gate)
            return _metadata.PortAllocations.GetValueOrDefault(replicaId);
    }

    public IReadOnlyDictionary<string, int> Allocations
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, int>(_metadata.PortAllocations, StringComparer.Ordinal);
        }
    }

    public void SetAllocatedPort(string replicaId, int port)
    {
        lock (_gate)
        {
            _metadata.PortAllocations[replicaId] = port;
            SaveUnsafe();
        }
    }

    public void ReleasePort(string replicaId)
    {
        lock (_gate)
        {
            if (_metadata.PortAllocations.Remove(replicaId))
                SaveUnsafe();
        }
    }

    public void PrunePortAllocations(IReadOnlySet<string> activeReplicaIds)
    {
        lock (_gate)
        {
            bool changed = false;
            foreach (string replicaId in _metadata.PortAllocations.Keys.ToArray())
            {
                if (!activeReplicaIds.Contains(replicaId))
                    changed |= _metadata.PortAllocations.Remove(replicaId);
            }

            if (changed)
                SaveUnsafe();
        }
    }

    public void Save()
    {
        lock (_gate)
            SaveUnsafe();
    }

    private void SaveUnsafe()
    {
        string marker = Path.Combine(DirectoryPath, MarkerFileName);
        string temporary = marker + ".tmp";
        string json = JsonSerializer.Serialize(_metadata, ProcessControlJsonContext.Default.LocalStateMetadata);
        File.WriteAllText(temporary, json);
        File.Move(temporary, marker, overwrite: true);
    }

    private static LocalStateMetadata ReadMarker(string marker)
    {
        try
        {
            LocalStateMetadata metadata = JsonSerializer.Deserialize(
                                              File.ReadAllText(marker),
                                              ProcessControlJsonContext.Default.LocalStateMetadata)
                                          ?? throw new LocalManifestException(
                                              $"State marker '{marker}' is empty.");
            if (metadata.SchemaVersion != 1)
            {
                throw new LocalManifestException(
                    $"State marker '{marker}' has unsupported schema version {metadata.SchemaVersion}.");
            }

            return metadata with
            {
                PortAllocations = new Dictionary<string, int>(
                    metadata.PortAllocations ?? new Dictionary<string, int>(),
                    StringComparer.Ordinal)
            };
        }
        catch (LocalManifestException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocalManifestException($"Invalid state marker '{marker}': {exception.Message}", exception);
        }
    }

    public void Dispose()
    {
        if (_loaded.IsPersistent || !Directory.Exists(DirectoryPath))
            return;

        string marker = Path.Combine(DirectoryPath, MarkerFileName);
        if (File.Exists(marker))
            Directory.Delete(DirectoryPath, recursive: true);
    }
}

public sealed class LocalPortAllocator
{
    private readonly LoadedLocalManifest _loaded;
    private readonly LocalStateStore _state;
    private readonly object _gate = new();
    private readonly HashSet<int> _reserved;
    private readonly Dictionary<string, int> _claimed = new(StringComparer.Ordinal);

    public LocalPortAllocator(LoadedLocalManifest loaded, LocalStateStore state)
    {
        _loaded = loaded;
        _state = state;
        _reserved = [loaded.Manifest.Cluster.GatewayPort];
        for (var index = 0; index < loaded.Manifest.Cluster.Nodes; index++)
        {
            _reserved.Add(loaded.Manifest.Cluster.HttpPortBase + index);
            _reserved.Add(loaded.Manifest.Cluster.RaftPortBase + index);
        }
    }

    public int? Reserve(string replicaId)
    {
        lock (_gate)
        {
            int? existing = _state.GetAllocatedPort(replicaId);
            if (existing.HasValue &&
                existing.Value >= _loaded.Manifest.ProcessPorts.From &&
                existing.Value <= _loaded.Manifest.ProcessPorts.To &&
                !_reserved.Contains(existing.Value) &&
                LocalManifestLoader.IsTcpPortAvailable(existing.Value))
            {
                _claimed[replicaId] = existing.Value;
                return existing.Value;
            }

            if (existing.HasValue)
            {
                _state.ReleasePort(replicaId);
                _claimed.Remove(replicaId);
            }

            HashSet<int> allocated = _state.Allocations
                .Where(item => !string.Equals(item.Key, replicaId, StringComparison.Ordinal))
                .Select(item => item.Value)
                .ToHashSet();

            for (int port = _loaded.Manifest.ProcessPorts.From;
                 port <= _loaded.Manifest.ProcessPorts.To;
                 port++)
            {
                if (_reserved.Contains(port) || allocated.Contains(port) ||
                    !LocalManifestLoader.IsTcpPortAvailable(port))
                {
                    continue;
                }

                _state.SetAllocatedPort(replicaId, port);
                _claimed[replicaId] = port;
                return port;
            }

            return null;
        }
    }

    public int? ReserveFixed(string replicaId, int port)
    {
        lock (_gate)
        {
            if (port is < 1 or > 65535 || _reserved.Contains(port))
                return null;

            int? existing = _state.GetAllocatedPort(replicaId);
            if (existing == port && LocalManifestLoader.IsTcpPortAvailable(port))
            {
                _claimed[replicaId] = port;
                return port;
            }

            if (existing.HasValue)
            {
                _state.ReleasePort(replicaId);
                _claimed.Remove(replicaId);
            }

            string[] allocatedByOtherIdentities = _state.Allocations
                .Where(item =>
                    !string.Equals(item.Key, replicaId, StringComparison.Ordinal) &&
                    item.Value == port)
                .Select(item => item.Key)
                .ToArray();
            if (allocatedByOtherIdentities.Any(_claimed.ContainsKey) ||
                !LocalManifestLoader.IsTcpPortAvailable(port))
            {
                return null;
            }

            // A fixed auxiliary port has priority over allocations persisted
            // by dynamic functions or automatic processes from an earlier run.
            foreach (string otherIdentity in allocatedByOtherIdentities)
                _state.ReleasePort(otherIdentity);

            _state.SetAllocatedPort(replicaId, port);
            _claimed[replicaId] = port;
            return port;
        }
    }

    public void Prune(IReadOnlySet<string> activeReplicaIds)
    {
        lock (_gate)
        {
            _state.PrunePortAllocations(activeReplicaIds);
            foreach (string replicaId in _claimed.Keys.ToArray())
            {
                if (!activeReplicaIds.Contains(replicaId))
                    _claimed.Remove(replicaId);
            }
        }
    }

    public void RejectAndRetry(string replicaId)
    {
        lock (_gate)
        {
            _claimed.Remove(replicaId);
            _state.ReleasePort(replicaId);
        }
    }

    public void Release(string replicaId)
    {
        lock (_gate)
        {
            _claimed.Remove(replicaId);
            _state.ReleasePort(replicaId);
        }
    }
}
