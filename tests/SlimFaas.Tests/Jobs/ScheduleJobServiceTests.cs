using MemoryPack;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Jobs;

public class ScheduleJobServiceTests
{
    private readonly Mock<IJobConfiguration> _jobConfigMock = new();
    private readonly Mock<IDatabaseService> _dbMock        = new();

    private readonly SlimFaasJobConfiguration _defaultConfig;
    private readonly ScheduleJobService        _sut; // System Under Test

    public ScheduleJobServiceTests()
    {
        // ---------- Arrange global fixtures ----------
        var publicJob = new SlimfaasJob(
            Image: "allowed:latest",
            ImagesWhitelist: new() { "allowed:latest" },
            Visibility: nameof(FunctionVisibility.Public));

        _defaultConfig = new SlimFaasJobConfiguration(new()
        {
            { JobConfiguration.Default, publicJob },
            { "test-func", publicJob }
        });

        _jobConfigMock.SetupGet(c => c.Configuration).Returns(_defaultConfig);

        _sut = new ScheduleJobService(_jobConfigMock.Object, _dbMock.Object);
    }

    // ---------------- CreateScheduleJobAsync ----------------

    [Fact(DisplayName = "Create – retourne une erreur quand l'expression cron est invalide")]
    public async Task Create_Should_Return_Error_When_Cron_Invalid()
    {
        // Arrange
        var job = new ScheduleCreateJob(
            Schedule: "cron invalid",           // Cron invalide
            Args: new() { "arg" });

        // Act
        var result = await _sut.CreateScheduleJobAsync("test-func", job, isMessageComeFromNamespaceInternal: true);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Null(result.Data);
        _dbMock.Verify(d => d.HashSetAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, byte[]>>()), Times.Never);
    }

    [Fact(DisplayName = "Create – refuse si fonction privée et appel externe")]
    public async Task Create_Should_Return_Error_When_Private_And_External_Call()
    {
        // Arrange : rend la fonction privée
        var privateJob = _defaultConfig.Configurations["test-func"] with
        {
            Visibility = nameof(FunctionVisibility.Private)
        };
        _defaultConfig.Configurations["test-func"] = privateJob;

        var job = new ScheduleCreateJob(
            Schedule: "* * * * *",
            Args: new() { "arg" });

        // Act
        var result = await _sut.CreateScheduleJobAsync("test-func", job, isMessageComeFromNamespaceInternal: false);

        // Assert
        Assert.Equal("visibility_private", result.Error.Key);
        _dbMock.VerifyNoOtherCalls();
    }

    [Fact(DisplayName = "Create – refuse l'image non autorisée")]
    public async Task Create_Should_Return_Error_When_Image_Not_Allowed()
    {
        // Arrange
        var job = new ScheduleCreateJob(
            Schedule: "* * * * *",
            Args: new() { "arg" },
            Image: "forbidden:tag");

        // Act
        var result = await _sut.CreateScheduleJobAsync("test-func", job, isMessageComeFromNamespaceInternal: true);

        // Assert
        Assert.Equal("image_not_allowed", result.Error?.Key);
    }

    [Fact(DisplayName = "Create – stocke et renvoie l'identifiant quand tout est valide")]
    public async Task Create_Should_Store_And_Return_Id_When_Valid()
    {
        // Arrange
        var job = new ScheduleCreateJob(
            Schedule: "* * * * *",
            Args: new() { "arg1", "arg2" },
            Image: "allowed:latest");

        Dictionary<string, byte[]>? persisted = null;
        _dbMock.Setup(d => d.HashSetAsync(
                "ScheduleJob:test-func",
                It.IsAny<IDictionary<string, byte[]>>(),
                It.IsAny<long?>()))
            .Callback<string, IDictionary<string, byte[]>, long?>((_, dict, __) =>
                persisted = new Dictionary<string, byte[]>(dict)
            );

        // Act
        var result = await _sut.CreateScheduleJobAsync("test-func", job, isMessageComeFromNamespaceInternal: true);

        // Assert
        Assert.NotNull(result.Data);
        Assert.False(string.IsNullOrWhiteSpace(result.Data!.Id));

        Assert.NotNull(persisted);
        Assert.Single(persisted!);
        Assert.Contains(result.Data!.Id, persisted!.Keys);
    }

    // ---------------- ListScheduleJobAsync ----------------

    [Fact(DisplayName = "List – renvoie une liste vide si aucune entrée")]
    public async Task List_Should_Return_Empty_When_No_Schedules()
    {
        // Arrange
        _dbMock.Setup(d => d.HashGetAllAsync("ScheduleJob:test-func"))
               .ReturnsAsync(new Dictionary<string, byte[]>());

        // Act
        var list = await _sut.ListScheduleJobAsync("test-func");

        // Assert
        Assert.Empty(list);
    }

    [Fact(DisplayName = "List – désérialise et renvoie les jobs planifiés")]
    public async Task List_Should_Return_Schedules_When_Present()
    {
        // Arrange
        var scheduleJob = new ScheduleCreateJob(
            Schedule: "0 0 * * *",
            Args: new() { "a" });

        var bytes = MemoryPackSerializer.Serialize(scheduleJob);

        _dbMock.Setup(d => d.HashGetAllAsync("ScheduleJob:test-func"))
               .ReturnsAsync(new Dictionary<string, byte[]> { { "schedule-id", bytes } });

        // Act
        var list = await _sut.ListScheduleJobAsync("test-func");

        // Assert
        var job = Assert.Single(list);
        Assert.Equal("schedule-id", job.Id);
        Assert.Equal("0 0 * * *", job.Schedule);
    }

    // ---------------- DeleteScheduleJobAsync ----------------

    [Fact(DisplayName = "Delete – refuse si fonction privée et appel externe")]
    public async Task Delete_Should_Return_Error_When_Private_And_External_Call()
    {
        // Arrange : fonction privée
        var privateJob = _defaultConfig.Configurations["test-func"] with
        {
            Visibility = nameof(FunctionVisibility.Private)
        };
        _defaultConfig.Configurations["test-func"] = privateJob;

        // Act
        var result = await _sut.DeleteScheduleJobAsync("test-func", "any-id", isMessageComeFromNamespaceInternal: false);

        // Assert
        Assert.Equal("visibility_private", result.Error?.Key);
    }

    [Fact(DisplayName = "Delete – renvoie not_found si l'ID n'existe pas")]
    public async Task Delete_Should_Return_NotFound_When_Id_Missing()
    {
        // Arrange
        _dbMock.Setup(d => d.HashGetAllAsync("ScheduleJob:test-func"))
               .ReturnsAsync(new Dictionary<string, byte[]>());

        // Act
        var result = await _sut.DeleteScheduleJobAsync("test-func", "missing", isMessageComeFromNamespaceInternal: true);

        // Assert
        Assert.Equal(ScheduleJobService.NotFound, result.Error?.Key);
    }

    [Fact(DisplayName = "Delete – supprime et renvoie l'ID quand l'entrée existe")]
    public async Task Delete_Should_Remove_And_Return_Id_When_Present()
    {
        // Arrange
        var id = "existing-id";
        _dbMock.Setup(d => d.HashGetAllAsync("ScheduleJob:test-func"))
               .ReturnsAsync(new Dictionary<string, byte[]> { { id, [] } });

        // Act
        var result = await _sut.DeleteScheduleJobAsync("test-func", id, isMessageComeFromNamespaceInternal: true);

        // Assert
        Assert.Equal(id, result.Data);
        _dbMock.Verify(d => d.HashSetDeleteAsync("ScheduleJob:test-func", id), Times.Once);
    }
}
