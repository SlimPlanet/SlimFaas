namespace SlimFaas;

public static class EnvironmentVariables
{
    public const string SlimFaasAllowUnsecureSSL = "SLIMFAAS_ALLOW_UNSECURE_SSL";
    public const bool SlimFaasAllowUnsecureSSLDefault = false;

    public const string SlimFaasJobsConfiguration = "SLIMFAAS_JOBS_CONFIGURATION";

    public const string SlimFaasSubscribeEvents = "SLIMFAAS_SUBSCRIBE_EVENTS";
    public const string SlimFaasSubscribeEventsDefault = "";

    public const string SlimFaasCorsAllowOrigin = "SLIMFAAS_CORS_ALLOW_ORIGIN";
    public const string SlimFaasCorsAllowOriginDefault = "*";

    public const string SlimFaasMaxRequestBodySize = "SLIMFASS_MAX_REQUEST_BODY_SIZE";
    public const long SlimFaasMaxRequestBodySizeDefault = 524288000;

    public const string SlimWorkerDelayMilliseconds = "SLIM_WORKER_DELAY_MILLISECONDS";
    public const int SlimWorkerDelayMillisecondsDefault = 50;

    public const string SlimJobsWorkerDelayMilliseconds = "SLIM_JOBS_WORKER_DELAY_MILLISECONDS";
    public const int SlimJobsWorkerDelayMillisecondsDefault = 1000;

    public const string SlimFaasPorts = "SLIMFAAS_PORTS";
    public const string BaseSlimDataUrl = "BASE_SLIMDATA_URL";
    public const string BaseSlimDataUrlDefault = "http://{pod_name}.slimfaas.default.svc.cluster.local:3262/";


    public const int SlimProxyMiddlewareTimeoutWaitWakeSyncFunctionMilliSecondsDefault = 30000;

    public const string TimeMaximumWaitForAtLeastOnePodStartedForSyncFunction =
        "TIME_MAXIMUM_WAIT_FOR_AT_LEAST_ONE_POD_STARTED_FOR_SYNC_FUNCTION";

    public const string ReplicasSynchronisationWorkerDelayMilliseconds =
        "REPLICAS_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS";

    public const int ReplicasSynchronizationWorkerDelayMillisecondsDefault = 1000;

    public const string HistorySynchronisationWorkerDelayMilliseconds =
        "HISTORY_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS";

    public const int HistorySynchronizationWorkerDelayMillisecondsDefault = 500;

    public const string ScaleReplicasWorkerDelayMilliseconds = "SCALE_REPLICAS_WORKER_DELAY_MILLISECONDS";
    public const int ScaleReplicasWorkerDelayMillisecondsDefault = 1000;

    public const string HealthWorkerDelayMilliseconds = "HEALTH_WORKER_DELAY_MILLISECONDS";
    public const int HealthWorkerDelayMillisecondsDefault = 1000;

    public const string HealthWorkerDelayToExitSeconds = "HEALTH_WORKER_DELAY_TO_EXIT_SECONDS";
    public const int HealthWorkerDelayToExitSecondsDefault = 60;

    public const string HealthWorkerDelayToStartHealthCheckSeconds = "HEALTH_WORKER_DELAY_TO_START_HEALTH_CHECK_SECONDS";
    public const int HealthWorkerDelayToStartHealthCheckSecondsDefault = 20;

    public const string PodScaledUpByDefaultWhenInfrastructureHasNeverCalled =
        "POD_SCALED_UP_BY_DEFAULT_WHEN_INFRASTRUCTURE_HAS_NEVER_CALLED";

    public const bool PodScaledUpByDefaultWhenInfrastructureHasNeverCalledDefault = false;

    public const string BaseFunctionUrl = "BASE_FUNCTION_URL";
    public const string BaseFunctionUrlDefault = "http://{function_name}:8080/";

    public const string BaseFunctionPodUrl = "BASE_FUNCTION_POD_URL";
    public const string BaseFunctionPodUrlDefault = "http://{pod_name}.{function_name}:8080/";

    public const string Namespace = "NAMESPACE";
    public const string NamespaceDefault = "default";

    public const string MockKubernetesFunctions = "MOCK_KUBERNETES_FUNCTIONS";
    public const string MockKubernetesFunctionsDefault = "";

    public const string SlimDataDirectory = "SLIMDATA_DIRECTORY";
    public const string SlimDataConfiguration = "SLIMDATA_CONFIGURATION";

    public const bool SlimDataAllowColdStartDefault = false;
    public static readonly int[] SlimFaasPortsDefault = { 5000 };

    public static string HostnameDefault = "slimfaas-1";

    public static string GetTemporaryDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        if (File.Exists(tempDirectory))
        {
            return GetTemporaryDirectory();
        }

        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    public static IDictionary<string, IList<string>> ReadSlimFaasSubscribeEvents<T>(ILogger<T> logger, string environmentVariableName, string defaultValue)
    {
        string valueString = Environment.GetEnvironmentVariable(environmentVariableName) ?? defaultValue;
        var results = new Dictionary<string, IList<string>>();
        if (!string.IsNullOrEmpty(valueString))
        {
            //"my-event-name1=>http://localhost:5002;http://localhost:5003,my-event-name2;http://localhost:5002"
            var events = valueString.Split(',');
            foreach (var @event in events)
            {
                var eventParts = @event.Split("=>");
                if (eventParts.Length < 2)
                {
                    logger.LogWarning("Cannot parse the event {Event} with value {EventValue}", @event, valueString);
                    continue;
                }

                var eventKey = eventParts[0];
                var urls = eventParts[1].Split(";");
                results[eventKey] = urls;
            }

            return results;
        }

        return results;
    }

    public static int ReadInteger<T>(ILogger<T> logger, string environmentVariableName, int defaultInteger)
    {
        string valueString = Environment.GetEnvironmentVariable(environmentVariableName) ?? defaultInteger.ToString();
        if (int.TryParse(valueString, out int value))
        {
            return value;
        }

        logger.LogWarning(
            "Cannot parse to int the environment variable {EnvironmentVariableName} with value {EnvironmentVariableValue}. Using default value {DefaultDelay}",
            environmentVariableName, valueString, defaultInteger);
        return defaultInteger;
    }

    public static long ReadLong<T>(ILogger<T>? logger, string environmentVariableName, long defaultLong)
    {
        string valueString = Environment.GetEnvironmentVariable(environmentVariableName) ?? defaultLong.ToString();
        if (long.TryParse(valueString, out long value))
        {
            return value;
        }

        logger?.LogWarning(
            "Cannot parse to int the environment variable {EnvironmentVariableName} with value {EnvironmentVariableValue}. Using default value {DefaultDelay}",
            environmentVariableName, valueString, defaultLong);

        return defaultLong;
    }

    public static bool ReadBoolean( string environmentVariableName, bool defaultBoolean)
    {
        string valueString = Environment.GetEnvironmentVariable(environmentVariableName) ?? defaultBoolean.ToString();
        if (bool.TryParse(valueString, out bool value))
        {
            return value;
        }
        return defaultBoolean;
    }
    public static bool ReadBoolean<T>(ILogger<T> logger, string environmentVariableName, bool defaultBoolean)
    {
        string valueString = Environment.GetEnvironmentVariable(environmentVariableName) ?? defaultBoolean.ToString();
        if (bool.TryParse(valueString, out bool value))
        {
            return value;
        }

        logger.LogWarning(
            "Cannot parse to boolean the environment variable {EnvironmentVariableName} with value {EnvironmentVariableValue}. Using default value {DefaultDelay}",
            environmentVariableName, valueString, defaultBoolean);
        return defaultBoolean;
    }

    public static int[] ReadIntegers(string name, int[] defaultNames)
    {
        List<int> ports = new List<int>();
        string? slimFaasPorts = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(slimFaasPorts))
        {
            return defaultNames;
        }

        string[] splits = slimFaasPorts.Split(',');
        foreach (string split in splits)
        {
            if (int.TryParse(split, out int value))
            {
                ports.Add(value);
            }
        }

        return ports.ToArray();
    }
}
