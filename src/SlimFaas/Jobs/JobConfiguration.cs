﻿﻿using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Jobs;

public interface IJobConfiguration
{
    SlimFaasJobConfiguration Configuration { get; }
}

public class JobConfiguration : IJobConfiguration
{
    public const string Default = "Default";

    public SlimFaasJobConfiguration Configuration { get; }


    public JobConfiguration(IOptions<SlimFaasOptions> slimFaasOptions, ILogger<JobConfiguration> logger)
    {
        SlimFaasJobConfiguration? slimfaasJobConfiguration = null;
        Dictionary<string, string> resources = new();
        resources.Add("cpu", "100m");
        resources.Add("memory", "100Mi");
        CreateJobResources createJobResources = new(resources, resources);
        SlimfaasJob defaultSlimfaasJob = new("", new List<string>(), createJobResources);
        try
        {
            string? json = slimFaasOptions.Value.JobsConfiguration;
            if(!string.IsNullOrEmpty(json))
            {
                json = JsonMinifier.MinifyJson(json);
            }
            if (!string.IsNullOrEmpty(json))
            {
                logger.LogInformation("JobConfiguration: {Json}", json);
                slimfaasJobConfiguration = JsonSerializer.Deserialize(json, SlimfaasJobConfigurationSerializerContext.Default.SlimFaasJobConfiguration);

                var configurations = new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase);
                if (slimfaasJobConfiguration?.Configurations != null)
                {
                    foreach (var (key, value) in slimfaasJobConfiguration.Configurations)
                    {
                        configurations[key] = value;
                    }
                }

                if (slimfaasJobConfiguration != null)
                {
                    slimfaasJobConfiguration = slimfaasJobConfiguration with { Configurations = configurations };
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error parsing SlimFaas job configuration");
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
