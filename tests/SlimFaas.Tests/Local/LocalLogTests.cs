using System.Text;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Logs;

namespace SlimFaas.Tests.Local;

public sealed class LocalLogTests
{
    [Fact]
    public async Task Final_append_between_eof_and_process_completion_is_drained()
    {
        string path = Path.GetTempFileName();
        try
        {
            bool appended = false;
            await using var file = new FollowingLogFile(path, async _ =>
            {
                if (!appended) { appended = true; await File.AppendAllTextAsync(path, "last output\n"); }
                return false;
            });
            var lines = new List<LogReadItem>();
            await foreach (var line in LogText.ReadLinesAsync(file, default)) lines.Add(line);
            Assert.Equal("last output", Assert.Single(lines).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Timestamp_changes_on_the_same_file_do_not_replay_retained_lines()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "already consumed\n");
            await using var file = new FollowingLogFile(path, _ => Task.FromResult(false));
            await using var reader = LogText.ReadLinesAsync(file, default).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1));
            Assert.False(await reader.MoveNextAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task File_tail_starts_at_last_ten_thousand_lines_and_follows_append_then_completion()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path, Enumerable.Range(1, 12000).Select(n => $"line {n}"));
            bool running = true;
            await using var file = new FollowingLogFile(path, _ => Task.FromResult(running));
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var reader = LogText.ReadLinesAsync(file, ct.Token).GetAsyncEnumerator();
            for (int i = 2001; i <= 12000; i++)
            { Assert.True(await reader.MoveNextAsync()); Assert.Equal($"line {i}", reader.Current.Text); }
            await File.AppendAllTextAsync(path, "live 🍋\n");
            Assert.True(await reader.MoveNextAsync()); Assert.Equal("live 🍋", reader.Current.Text);
            running = false;
            Assert.False(await reader.MoveNextAsync());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_rotation_and_truncation_are_visible_and_do_not_replay_the_old_file(bool rotate)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "a long previous entry\n");
            await using var file = new FollowingLogFile(path, _ => Task.FromResult(true));
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var reader = LogText.ReadLinesAsync(file, ct.Token).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            if (rotate) File.Move(path, path + ".old");
            await File.WriteAllTextAsync(path, "new\n");
            var foundNotice = false;
            while (await reader.MoveNextAsync())
            {
                foundNotice |= reader.Current.Text.Contains("rotated or truncated");
                if (reader.Current.Text == "new") break;
            }
            Assert.True(foundNotice); Assert.Equal("new", reader.Current.Text);
        }
        finally { File.Delete(path); File.Delete(path + ".old"); }
    }

    [Fact]
    public async Task Idle_file_reads_cancel_and_removed_or_symlinked_files_are_rejected()
    {
        string path = Path.GetTempFileName(), link = path + ".link";
        try
        {
            await using var file = new FollowingLogFile(path, _ => Task.FromResult(true));
            using var stopping = new CancellationTokenSource();
            var pending = file.ReadAsync(new byte[100], stopping.Token).AsTask();
            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(link, path);
                Assert.Throws<FileNotFoundException>(() => new FollowingLogFile(link, _ => Task.FromResult(true)));
            }
            File.Delete(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => file.ReadAsync(new byte[100]).AsTask());
        }
        finally { File.Delete(link); File.Delete(path); }
    }

    [Fact]
    public async Task Byte_limited_tail_discards_partial_utf8_line_at_the_boundary()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('x', LogLimits.Bytes + 100) + "\nlast 🍋\n");
            await using var file = new FollowingLogFile(path, _ => Task.FromResult(false));
            var lines = new List<LogReadItem>();
            await foreach (var line in LogText.ReadLinesAsync(file, default)) lines.Add(line);
            Assert.Equal("last 🍋", Assert.Single(lines).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Native_sources_cover_functions_jobs_nodes_but_not_debug_processes_or_unknown_resources()
    {
        string directory = Path.Combine(Path.GetTempPath(), "slimfaas-log-test-" + Guid.NewGuid().ToString("N"));
        var manifest = new LocalManifest { Functions = new() { ["f"] = new(), ["ide"] = new() { DebugUrl = "http://localhost:5000" } }, Jobs = new() { ["job"] = new() } };
        var loaded = new LoadedLocalManifest(manifest, "test.yaml", directory, directory, false);
        using var state = LocalStateStore.Open(loaded, false);
        var pod = new PodInformation("f-0", false, false, "127.0.0.1", "f", ResourceVersion: "one");
        var topology = new DeploymentsInformations([new DeploymentInformation("f", "test", [pod], new(), 1)],
            new SlimFaasDeploymentInformation(1, [new("slimfaas-0", false, false, "127.0.0.1", "slimfaas", ResourceVersion: "one")]), []);
        var jobs = new List<Job> { new("job-slimfaas-job-123", JobStatus.Succeeded, [], [], "element", 1, 2) };
        var provider = new LocalInstanceLogs(loaded, state, () => topology, () => jobs);
        try
        {
            foreach (var target in new[] { new LogTarget("function", "f", "f-0"), new("job", "job", jobs[0].Name), new("slimfaas", "slimfaas", "slimfaas-0") })
            {
                var source = Assert.Single((await provider.GetSourcesAsync(target, default)).Sources);
                Assert.Equal(source.Id, Assert.Single((await provider.GetSourcesAsync(target, default)).Sources).Id);
                await File.WriteAllTextAsync(Path.Combine(state.LogsDirectory, source.Name + ".log"), "retained line\n");
                var lines = new List<LogReadItem>();
                await foreach (var line in provider.ReadAsync(source.Id, default)) lines.Add(line);
                Assert.Equal("retained line", Assert.Single(lines).Text);
            }
            Assert.Empty((await provider.GetSourcesAsync(new("function", "ide", "ide-debug"), default)).Sources);
            Assert.Empty((await provider.GetSourcesAsync(new("function", "unknown", "../../secret"), default)).Sources);
            var old = Assert.Single((await provider.GetSourcesAsync(new("function", "f", "f-0"), default)).Sources);
            topology = topology with { Functions = [topology.Functions[0] with { Pods = [pod with { ResourceVersion = "two" }] }] };
            await using var reader = provider.ReadAsync(old.Id, default).GetAsyncEnumerator();
            await Assert.ThrowsAsync<FileNotFoundException>(() => reader.MoveNextAsync().AsTask());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Native_demo_exposure_can_be_overridden_without_enabling_other_manifests()
    {
        Assert.False(new LocalClusterManifest().ExposeLogs);
        var loaded = LocalManifestLoader.Load([Path.GetFullPath("../../../../../slimfaas.local.yaml", AppContext.BaseDirectory)], []);
        Assert.True(loaded.Manifest.Cluster.ExposeLogs);
    }
}
