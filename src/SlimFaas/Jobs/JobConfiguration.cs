using System.Text.Json;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;

public interface IJobConfiguration
{
    SlimFaasJobConfiguration Configuration { get; }
}

public class JobConfiguration : IJobConfiguration
{
    public const string Default = "Default";

    public SlimFaasJobConfiguration Configuration { get; }


    public JobConfiguration(string? json = null)
    {
        SlimFaasJobConfiguration? slimfaasJobConfiguration = null;
        Dictionary<string, string> resources = new();
        resources.Add("cpu", "100m");
        resources.Add("memory", "100Mi");
        CreateJobResources createJobResources = new(resources, resources);
        SlimfaasJob defaultSlimfaasJob = new("", new List<string>(), createJobResources);
        try
        {
            json ??= Environment.GetEnvironmentVariable(EnvironmentVariables.SlimFaasJobsConfiguration);
            if(!string.IsNullOrEmpty(json))
            {
                json = JsonMinifier.MinifyJson(json);
            }
            if (!string.IsNullOrEmpty(json))
            {
                Console.WriteLine("JobConfiguration: ");
                Console.WriteLine(json);
                slimfaasJobConfiguration = JsonSerializer.Deserialize(json, SlimfaasJobConfigurationSerializerContext.Default.SlimFaasJobConfiguration);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error parsing SlimFaas job configuration: " + ex.Message);
        }

        if (slimfaasJobConfiguration is null or { Configurations: null })
        {
            slimfaasJobConfiguration = new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase));
        }

        if (!slimfaasJobConfiguration.Configurations.TryAdd(Default, defaultSlimfaasJob))
        {
            if (slimfaasJobConfiguration.Configurations[Default].Resources == null)
            {
                var actualResources = slimfaasJobConfiguration.Configurations[Default];
                slimfaasJobConfiguration.Configurations[Default] = actualResources with { Resources = createJobResources };
            }
        }

        Configuration = slimfaasJobConfiguration;
    }
}
