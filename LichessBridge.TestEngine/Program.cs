using System.Text.Json;

using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "responses.json")));
var responses = configuration.RootElement;
var hasStartedPlaying = false;

if (responses.TryGetProperty("uci", out var uci) && uci.ValueKind is JsonValueKind.Object)
{
    await RunUciAsync(uci);

    return;
}

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

static async Task RunUciAsync(JsonElement settings)
{
    var bestMoves = settings.GetProperty("BestMoves").EnumerateArray().Select(value => value.GetString()).ToArray();
    var searchIndex = 0;
    string? activeMove = null;
    var searchMode = string.Empty;

    while (Console.ReadLine() is { } command)
    {
        if (command is "quit")
        {
            return;
        }

        if (command is "uci")
        {
            Console.WriteLine("id name Local UCI Test Engine");
            Console.WriteLine("id author LichessBridge");

            foreach (var option in settings.GetProperty("Options").EnumerateArray())
            {
                Console.WriteLine(option.GetString());
            }

            if (settings.GetProperty("OmitUciOk").GetBoolean() is false)
            {
                Console.WriteLine("uciok");
            }
        }

        if (command is "isready" && settings.GetProperty("OmitReadyOk").GetBoolean() is false)
        {
            Console.WriteLine("readyok");
        }

        if (command.StartsWith("go ", StringComparison.Ordinal))
        {
            activeMove = bestMoves[Math.Min(searchIndex++, bestMoves.Length - 1)];
            searchMode = command.StartsWith("go ponder ", StringComparison.Ordinal) ? "ponder" : command is "go infinite" ? "analysis" : "move";

            var infoBySearch = settings.GetProperty("InfoBySearch");
            var searchInfo = infoBySearch.GetArrayLength() > 0 ? infoBySearch[Math.Min(searchIndex - 1, infoBySearch.GetArrayLength() - 1)] : settings.GetProperty("Info");

            foreach (var info in searchInfo.EnumerateArray())
            {
                Console.WriteLine(info.GetString());
            }

            if (searchMode is "move" && settings.GetProperty("WaitForStop").GetBoolean() is false)
            {
                Console.WriteLine(activeMove);
                activeMove = null;
            }
        }

        if (command is "ponderhit" && activeMove is not null)
        {
            Console.WriteLine(activeMove);
            activeMove = null;
        }

        if (command is "stop" && activeMove is not null)
        {
            await Task.Delay(settings.GetProperty("StopDelayMilliseconds").GetInt32());

            if (settings.GetProperty("OmitStopReply").GetBoolean() is false)
            {
                Console.WriteLine(searchMode is "move" ? activeMove : settings.GetProperty("StopMove").GetString());
                activeMove = null;
            }
        }
    }
}
