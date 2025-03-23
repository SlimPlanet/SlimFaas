using Microsoft.AspNetCore.Mvc.Testing;

namespace SlimFaas.Tests;

public class ProgramShould
{
    [Fact]
    public async Task TestRootEndpoint()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariables.BaseSlimDataUrl, "http://localhost:3262");
        Environment.SetEnvironmentVariable(EnvironmentVariables.SlimDataConfiguration, "{\"coldStart\":\"true\"}");
        Environment.SetEnvironmentVariable(EnvironmentVariables.MockKubernetesFunctions,
            "{\"Functions\":[{\"Name\":\"fibonacci1\",\"NumberParallelRequest\":1},{\"Name\":\"fibonacci2\",\"NumberParallelRequest\":1}],\"Slimfaas\":[{\"Name\":\"slimfaas-1\"}]}");
        await using WebApplicationFactory<Program> application = new();
        using HttpClient client = application.CreateClient();

        string response = await client.GetStringAsync("http://localhost:5000/health");

        Assert.Equal("OK", response);
    }
}
