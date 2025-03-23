namespace SlimFaas;

public class HostPort
{
    public static bool IsSamePort(int? requestPort, int[] ports)
    {
        Console.WriteLine("IsSamePort");
        if (requestPort == null)
        {
            return true;
        }

        foreach (int port in ports)
        {
            Console.WriteLine($" port {port} == requestPort {requestPort}");
            if (port == requestPort)
            {
                return true;
            }
        }

        return false;
    }
}
