using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Jobs;

public interface IJobConfiguration
{
    SlimFaasJobConfiguration Configuration { get; set; }

    Task SyncJobsConfigurationAsync();
}

public class JobConfiguration : IJobConfiguration
{
    public const string Default = "Default";


    private SlimFaasJobConfiguration _configuration;
    private readonly SlimFaasJobConfiguration _initialConfiguration;

    public SlimFaasJobConfiguration Configuration
    {
        get => _configuration;
        set => _configuration = value;
    }
    private readonly IKubernetesService _service;

    private readonly string _namespace;

    public JobConfiguration(IOptions<SlimFaasOptions> slimFaasOptions, IKubernetesService kubernetesService, ILogger<JobConfiguration> logger, INamespaceProvider namespaceProvider)
    {
        _namespace = namespaceProvider.CurrentNamespace;
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

        _configuration = slimfaasJobConfiguration;
        _initialConfiguration = slimfaasJobConfiguration;
        _service = kubernetesService;
    }

    private SlimFaasJobConfiguration MergeJobConfigurations(SlimFaasJobConfiguration configuration)
    {
        SlimFaasJobConfiguration defaultConfiguration = _initialConfiguration;

        if (defaultConfiguration.Schedules == null)
        {
            defaultConfiguration = defaultConfiguration with { Schedules = new Dictionary<string, IList<ScheduleCreateJob>>(StringComparer.OrdinalIgnoreCase)};
        }

        foreach (var kvp in configuration.Configurations)
        {
            if (kvp.Key.Equals(Default, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            defaultConfiguration.Configurations[kvp.Key] = kvp.Value;
        }

        if (configuration.Schedules != null)
        {
            foreach (var kvp in configuration.Schedules)
            {
                if (kvp.Key.Equals(Default, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!defaultConfiguration.Schedules.TryAdd(kvp.Key, kvp.Value))
                {
                    defaultConfiguration.Schedules[kvp.Key] = kvp.Value;
                }
            }
        }


        return defaultConfiguration;
    }

    public async Task SyncJobsConfigurationAsync()
    {
        var configuration = await _service.ListJobsConfigurationAsync(_namespace);

        if (configuration != null)
            Interlocked.Exchange(ref _configuration, MergeJobConfigurations(configuration));
    }
}
