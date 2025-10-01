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
}
