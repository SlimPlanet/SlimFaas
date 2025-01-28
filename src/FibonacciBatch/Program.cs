

foreach(string arg in args){
    Console.WriteLine($"Calculating Fibonacci for {arg}");
    int i = int.Parse(arg);
    var fibonacci = new Fibonacci();
    var result = fibonacci.Run(i);
    Console.WriteLine($"Fibonacci for {arg} is {result}");
}

internal class Fibonacci
{
    public int Run(int i)
    {
        if (i <= 2)
        {
            return 1;
        }

        return Run(i - 1) + Run(i - 2);
    }
}
