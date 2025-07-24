using SlimData;

switch (args.LongLength)
{
    case 0:
    case 1:
        Console.WriteLine("Port number and protocol are not specified !");
        break;
    case 2:
        await Starter.StartNode(args[0], args[1]);
        break;
}