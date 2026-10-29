using System.Text.Json;

using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "responses.json")));
var responses = configuration.RootElement;
var hasStartedPlaying = false;

while (Console.ReadLine() is { } command)
{
    if (command == "quit")
    {
        break;
    }

    if (command == "protover 2")
    {
        Console.WriteLine(responses.GetProperty("handshakeResponse").GetString());
    }

    if (command == "go")
    {
        WriteResponse("analysisResponse");

        if (responses.GetProperty("waitForEmergencyMove").GetBoolean() is false)
        {
            WriteResponse("moveResponse");
        }

        hasStartedPlaying = true;
    }

    if (command == "?")
    {
        WriteResponse("moveResponse");
    }

    if (hasStartedPlaying && command.Length == 4 && command[0] is >= 'a' and <= 'h' && command[1] is >= '1' and <= '8')
    {
        WriteResponse("ponderResponse");
        hasStartedPlaying = false;
    }

    if (command == "hint")
    {
        WriteResponse("hintResponse");
    }
}

void WriteResponse(string name)
{
    if (responses.GetProperty(name).GetString() is { } response)
    {
        Console.WriteLine(response);
    }
}
