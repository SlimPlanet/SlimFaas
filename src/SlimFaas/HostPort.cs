namespace SlimFaas;

public class HostPort
{
    public static bool IsSamePort(int? requestPort, int[] ports)
    {
        return requestPort == null || ports.Any(port => port == requestPort);
    }
}
