using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalCommandTests
{
    [Fact]
    public void Parse_UsesTheDefaultManifestOnlyWhenNoFileIsSpecified()
    {
        LocalCommand.ParsedCommand command = LocalCommand.Parse(["local", "validate"]);

        Assert.Equal("validate", command.Verb);
        Assert.Equal([LocalManifestLoader.DefaultFileName], command.Files);
        Assert.Empty(command.EnvironmentFiles);
        Assert.False(command.Clean);
    }

    [Fact]
    public void Parse_PreservesFileAndEnvironmentFileOrder()
    {
        LocalCommand.ParsedCommand command = LocalCommand.Parse(
        [
            "local", "up",
            "-f", "base.yaml",
            "--env-file", "base.env",
            "--file", "dev.yaml",
            "--env-file", "dev.env",
            "--clean"
        ]);

        Assert.Equal(["base.yaml", "dev.yaml"], command.Files);
        Assert.Equal(["base.env", "dev.env"], command.EnvironmentFiles);
        Assert.True(command.Clean);
    }

    [Fact]
    public void Parse_RejectsCleanForValidate()
    {
        Assert.Throws<LocalManifestException>(
            () => LocalCommand.Parse(["local", "validate", "--clean"]));
    }
}
