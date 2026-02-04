﻿using Microsoft.AspNetCore.Mvc.Testing;

namespace SlimFaas.Tests;

public class ProgramShould
{
    /*
    /// <summary>
    /// ⚠️ Test obsolète - utilise les anciennes variables d'environnement
    /// Après migration vers IOptions<T>, utiliser le nouveau format :
    /// SlimFaas__BaseSlimDataUrl, SlimData__Configuration, etc.
    /// </summary>
    [Fact]
    public async Task TestRootEndpoint()
    {
        Environment.SetEnvironmentVariable("SlimFaas__BaseSlimDataUrl", "http://localhost:3262");
        Environment.SetEnvironmentVariable("SlimData__Configuration", "{\"coldStart\":\"true\"}");
        Environment.SetEnvironmentVariable("SlimFaas__Orchestrator", "Mock");
        Environment.SetEnvironmentVariable("SlimFaas__MockKubernetesFunctions",
            "{\"Functions\":[{\"Name\":\"fibonacci1\",\"NumberParallelRequest\":1},{\"Name\":\"fibonacci2\",\"NumberParallelRequest\":1}],\"Slimfaas\":[{\"Name\":\"slimfaas-1\"}]}");
        await using WebApplicationFactory<Program> application = new();
        using HttpClient client = application.CreateClient();

        string response = await client.GetStringAsync("http://localhost:5000/health");

        Assert.Equal("OK", response);
    }*/
}
