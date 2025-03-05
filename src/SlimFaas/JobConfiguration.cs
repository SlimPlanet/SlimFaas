using System.Text.Json;
using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IJobConfiguration
{
    SlimfaasJobConfiguration Configuration { get; }
}

public class JobConfiguration : IJobConfiguration
{

    public SlimfaasJobConfiguration Configuration { get; }


    public JobConfiguration()
    {
        SlimfaasJobConfiguration? slimfaasJobConfiguration = null;
        Dictionary<string, string> resources = new();
        resources.Add("cpu", "100m");
        resources.Add("memory", "100Mi");
        CreateJobResources createJobResources = new(resources, resources);
        SlimfaasJob defaultSlimfaasJob = new("", new List<string>(), createJobResources);
        try
        {
            string? json = Environment.GetEnvironmentVariable(EnvironmentVariables.SlimFaasJobsConfiguration);

            if (!string.IsNullOrEmpty(json))
            {
                slimfaasJobConfiguration = JsonSerializer.Deserialize(json, SlimfaasJobConfigurationSerializerContext.Default.SlimfaasJobConfiguration);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error parsing SlimFaas job configuration: " + ex.Message);
        }

        if (slimfaasJobConfiguration is null or { Configurations: null })
        {
            slimfaasJobConfiguration = new SlimfaasJobConfiguration(new Dictionary<string, SlimfaasJob>());
        }

        if (!slimfaasJobConfiguration.Configurations.ContainsKey("Default"))
        {
            slimfaasJobConfiguration.Configurations.Add("Default", defaultSlimfaasJob);
        }
        else
        {
            if (slimfaasJobConfiguration.Configurations["Default"].Resources == null)
            {
                var actualResources = slimfaasJobConfiguration.Configurations["Default"];
                slimfaasJobConfiguration.Configurations["Default"] = actualResources with { Resources = createJobResources };
            }
        }
        Configuration = slimfaasJobConfiguration;
    }
}
