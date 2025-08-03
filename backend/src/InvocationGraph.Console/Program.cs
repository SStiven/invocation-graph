namespace InvocationGraph.UI;

internal class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Write the root folder path!");
        string? readLine = Console.ReadLine();

        while (Path.IsPathRooted(readLine))
        {
            Console.WriteLine("The text provided is not a rooted path");
            readLine = Console.ReadLine();
        }


    }
}
