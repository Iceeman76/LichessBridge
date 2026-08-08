while (Console.ReadLine() is { } command)
{
    if (command == "protover 2")
    {
        Console.WriteLine("feature myname=\"Fake WB/2 Engine\" usermove=1 done=1");
        Console.Out.Flush();
    }
    else if (command == "go")
    {
        Console.WriteLine("move e2e4");
        Console.Out.Flush();
    }
    else if (command == "quit")
    {
        return;
    }
}
