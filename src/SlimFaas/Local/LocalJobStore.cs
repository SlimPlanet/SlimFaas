using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SlimFaas.Kubernetes;

namespace SlimFaas.Local;

public sealed record LocalJobState(
    int SchemaVersion,
    ProcessCreateJobCommand Command,
    JobStatus Status,
    int Attempts,
    long StartTimestamp,
    DateTimeOffset NextStart,
    DateTimeOffset? FinishedAt,
    bool Deleted);

internal sealed class LocalJobStore(string stateDirectory)
{
    private readonly string _directory = Path.Combine(stateDirectory, "jobs");

    public IEnumerable<LocalJobState> ReadAll()
    {
        Directory.CreateDirectory(_directory);
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            using FileStream stream = File.OpenRead(path);
            LocalJobState state = JsonSerializer.Deserialize(stream, ProcessControlJsonContext.Default.LocalJobState)
                                  ?? throw new LocalManifestException($"Local job state '{path}' is empty.");
            if (state.SchemaVersion != 1 || state.Attempts < 1 ||
                !Enum.IsDefined(state.Status) || string.IsNullOrWhiteSpace(state.Command.JobFullName) ||
                !string.Equals(path, GetPath(state.Command.JobFullName), StringComparison.Ordinal))
            {
                throw new LocalManifestException($"Invalid or unsupported local job state '{path}'.");
            }
            yield return state;
        }
    }

    public void Save(LocalJobState state)
    {
        Directory.CreateDirectory(_directory);
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, ProcessControlJsonContext.Default.LocalJobState);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, GetPath(state.Command.JobFullName), overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Delete(string fullName) => File.Delete(GetPath(fullName));

    private string GetPath(string fullName)
        => Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fullName))) + ".json");
}
