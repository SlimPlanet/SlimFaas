namespace SlimFaas;

public class HostPort
{
    public static bool IsSamePort(int[] requestPorts, int[] ports)
    {
        foreach (int requestPort in requestPorts)
        {
            if (ports.Any(port => port == requestPort))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSamePort(int localPort, int hostPort, IList<int> ports)
    {
        for (var index = 0; index < ports.Count; index++)
        {
            int port = ports[index];
            if (port == localPort || port == hostPort)
            {
                return true;
            }
        }

        return false;
    }
}
