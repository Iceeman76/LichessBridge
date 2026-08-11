using System.Diagnostics;
using System.Globalization;
using LichessBridge.Domain;
using LichessBridge.Engine;
using LichessBridge.Status;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using static LichessBridge.Status.StatusFormatter;

namespace Bridge;

public sealed class ConsoleStatusDisplay : IStatusDisplay, IDisposable
{
    private const int LogTimestampWidth = 8;
    private const int LogLevelWidth = 5;
    private const int LogCategoryWidth = 24;
    private const int LogLayoutOverhead = 13;
    private static readonly Color PanelColor = new(78, 92, 102);
    private static readonly Color AccentColor = new(214, 158, 46);
    private readonly Lock stateLock = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim renderSignal = new(0, 1);
    private readonly Timer clockTimer;
    private readonly Task renderTask;
    private StatusHeader? header;
    private GameStatus? game;
    private EngineAnalysis? analysis;
    private EngineSearchStatus? searchStatus;
    private string? hint;
    private string status = "Starting...";
    private bool quitRequested;
    private IReadOnlyList<DashboardLogEntry> logEntries = [];
    private long gameUpdatedAt;

    public ConsoleStatusDisplay()
    {
        clockTimer = new Timer(UpdateClocks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        renderTask = Console.IsOutputRedirected ? Task.CompletedTask : RenderLiveAsync(stopping.Token);
    }

    public void ShowHeader(StatusHeader header)
    {
        lock (stateLock)
        {
            this.header = header;
        }

        RequestRender();
    }

    public void ShowGame(GameStatus game)
    {
        lock (stateLock)
        {
            this.game = game;
            gameUpdatedAt = Stopwatch.GetTimestamp();
        }

        RequestRender();
    }

    public void ShowAnalysis(EngineAnalysis? analysis)
    {
        lock (stateLock)
        {
            this.analysis = analysis;
        }

        RequestRender();
    }

    public void ShowSearchStatus(EngineSearchStatus? searchStatus)
    {
        lock (stateLock)
        {
            this.searchStatus = searchStatus;
        }

        RequestRender();
    }

    public void ShowHint(string? hint)
    {
        lock (stateLock)
        {
            this.hint = hint;
        }

        RequestRender();
    }

    public void ClearGame()
    {
        lock (stateLock)
        {
            game = null;
            analysis = null;
            searchStatus = null;
            hint = null;
        }

        RequestRender();
    }

    public void Show(string status)
    {
        lock (stateLock)
        {
            if (this.status == status)
            {
                return;
            }

            this.status = status;
        }

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(status);

            return;
        }

        RequestRender();
    }

    public void ShowQuit(bool requested)
    {
        lock (stateLock)
        {
            quitRequested = requested;
        }

        RequestRender();
    }

    public void ShowLogs(IReadOnlyList<DashboardLogEntry> entries)
    {
        lock (stateLock)
        {
            logEntries = entries;
        }

        RequestRender();
    }

    public void Dispose()
    {
        clockTimer.Dispose();
        stopping.Cancel();
        RequestRender();
        renderTask.GetAwaiter().GetResult();
        renderSignal.Dispose();
        stopping.Dispose();
    }

    private async Task RenderLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AnsiConsole.Live(CreateDashboard()).AutoClear(false).StartAsync(async context =>
            {
                try
                {
                    while (cancellationToken.IsCancellationRequested is false)
                    {
                        await renderSignal.WaitAsync(cancellationToken);
                        await Task.Delay(50, cancellationToken);

                        while (renderSignal.Wait(0))
                        {
                        }

                        context.UpdateTarget(CreateDashboard());
                        context.Refresh();
                    }
                }
                finally
                {
                    context.UpdateTarget(CreateDashboard());
                    context.Refresh();
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private IRenderable CreateDashboard()
    {
        lock (stateLock)
        {
            var sections = new List<IRenderable>();

            if (header is not null)
            {
                sections.Add(CreateHeaderPanel(header));
            }

            if (game is not null)
            {
                sections.Add(CreateGamePanel(game));
            }

            sections.Add(CreateStatusPanel());

            if (logEntries.Count > 0)
            {
                sections.Add(CreateLogPanel());
            }

            return new Rows(sections);
        }
    }

    private Panel CreateLogPanel()
    {
        var reservedRows = game is null ? 22 : 32;
        var panelHeight = Math.Max(3, AnsiConsole.Profile.Height - reservedRows);
        var maximumEntries = panelHeight - 2;
        var messageWidth = Math.Max(20, AnsiConsole.Profile.Width - LogTimestampWidth - LogLevelWidth - LogCategoryWidth - LogLayoutOverhead);
        var lines = new List<IRenderable>();

        foreach (var entry in logEntries.TakeLast(maximumEntries))
        {
            var level = entry.Level switch
            {
                LogLevel.Trace => "[dim]TRC[/]",
                LogLevel.Debug => "[grey]DBG[/]",
                LogLevel.Information => "[white]INF[/]",
                LogLevel.Warning => "[yellow]WRN[/]",
                LogLevel.Error => "[red]ERR[/]",
                LogLevel.Critical => "[bold red]CRT[/]",
                _ => "[dim]---[/]"
            };
            var category = entry.Category.Split('.').LastOrDefault() ?? entry.Category;
            var message = entry.Exception is null ? entry.Message : $"{entry.Message} — {entry.Exception.Message}";
            var normalizedMessage = message.ReplaceLineEndings(" ").Replace("\t", "  ", StringComparison.Ordinal);
            var timestamp = entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture).PadRight(LogTimestampWidth);
            var fixedCategory = Truncate(category, LogCategoryWidth).PadRight(LogCategoryWidth);
            var fixedLevel = $"{level}{new string(' ', LogLevelWidth - 3)}";
            var line = $"{Escape(timestamp)}  {fixedLevel}  {Escape(fixedCategory)}  {Escape(Truncate(normalizedMessage, messageWidth))}";
            lines.Add(new Markup(line));
        }

        return new Panel(new Rows(lines))
        {
            Header = new PanelHeader("[bold] RECENT LOG [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(PanelColor),
            Expand = true
        };
    }

    private Panel CreateStatusPanel()
    {
        var table = new Table().NoBorder().HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty));
        table.AddColumn(new TableColumn(string.Empty).RightAligned());
        var quit = quitRequested ? "✓ Quit" : string.Empty;
        var currentStatus = string.IsNullOrWhiteSpace(hint) || status.StartsWith("Waiting for", StringComparison.OrdinalIgnoreCase) is false ? status : $"{status} Pondering on {hint}...";
        table.AddRow(new Markup($"[bold #d69e2e]{Markup.Escape(currentStatus)}[/]"), new Text(quit));

        return new Panel(table)
        {
            Header = new PanelHeader("[bold] CURRENT STATUS [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(AccentColor),
            Expand = true
        };
    }

    private static Panel CreateHeaderPanel(StatusHeader currentHeader)
    {
        var engine = new Grid();
        engine.AddColumn();
        engine.AddColumn();
        engine.AddRow("[grey]Engine[/]", $"[bold #d69e2e]{Escape(currentHeader.EngineName)}[/] playing as [bold]{Escape(currentHeader.AccountName)}[/] on Lichess");
        engine.AddRow("[grey]Pondering[/]", currentHeader.Ponder ? "[bold green]ON[/]" : "[bold red]OFF[/]");
        engine.AddRow("[grey]Auto challenge[/]", currentHeader.AutoBotChallenge ? "[bold green]ON[/]" : "[bold red]OFF[/]");

        if (currentHeader.Ratings.Count > 0)
        {
            engine.AddEmptyRow();
        }

        foreach (var rating in currentHeader.Ratings)
        {
            engine.AddRow($"[grey]{Escape(FormatSpeed(rating.Speed))}[/]", $"[bold]{rating.Rating}[/]");
        }

        var filters = new Grid();
        filters.AddColumn();
        filters.AddColumn();
        filters.AddRow("[grey]CHALLENGES[/]", string.Empty);
        filters.AddRow(new Markup("[grey]Mode[/]"), FormatCheckboxes(currentHeader.Modes, ["casual", "rated"]));
        filters.AddRow(new Markup("[grey]Variant[/]"), FormatCheckboxes(currentHeader.Variants, ["standard"]));
        filters.AddRow(new Markup("[grey]Speed[/]"), FormatCheckboxes(currentHeader.Speeds, ["bullet", "blitz", "rapid"]));
        filters.AddEmptyRow();
        filters.AddRow("[grey]OPPONENTS[/]", string.Empty);
        filters.AddRow(new Markup("[grey]Type[/]"), FormatCheckboxes(currentHeader.Opponents, ["human", "bot"]));

        var layout = new Table().NoBorder().HideHeaders();
        layout.AddColumn(new TableColumn(string.Empty));
        layout.AddColumn(new TableColumn(string.Empty).Width(6));
        layout.AddColumn(new TableColumn(string.Empty));
        layout.AddRow(engine, new Text(string.Empty), filters);

        return new Panel(layout)
        {
            Header = new PanelHeader("[bold green] ● ONLINE [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(PanelColor),
            Expand = true
        };
    }

    private static Markup FormatCheckboxes(IEnumerable<string> selectedValues, IEnumerable<string> standardValues)
    {
        var selected = selectedValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = standardValues.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase);
        var checkboxes = values.Select(value => selected.Contains(value)
            ? $"[#7a9e7e][[x]][/] {Escape(FormatSpeed(value))}"
            : $"[grey][[ ]][/] {Escape(FormatSpeed(value))}");

        return new Markup(string.Join("   ", checkboxes));
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        return $"{value[..(maximumLength - 1)]}…";
    }

    private Panel CreateGamePanel(GameStatus currentGame)
    {
        var elapsed = Stopwatch.GetElapsedTime(gameUpdatedAt);
        var whiteTime = AdjustClock(currentGame.WhiteTimeMilliseconds, currentGame.ActiveColor is ChessColor.White, elapsed);
        var blackTime = AdjustClock(currentGame.BlackTimeMilliseconds, currentGame.ActiveColor is ChessColor.Black, elapsed);
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[grey]Game[/]", $"[bold]{Escape(FormatSpeed(currentGame.Mode))} {Escape(FormatSpeed(currentGame.Speed))}[/]  [#d69e2e]{Escape(currentGame.TimeControl)}[/]");
        grid.AddRow("[white]White[/]", Escape(FormatPlayer(currentGame.WhiteName, currentGame.WhiteTitle, currentGame.WhiteRating)));
        grid.AddRow("[grey]Black[/]", Escape(FormatPlayer(currentGame.BlackName, currentGame.BlackTitle, currentGame.BlackRating)));
        grid.AddRow("[grey]Last move[/]", $"[bold #d69e2e]{Escape(currentGame.LastMove)}[/]");
        grid.AddRow("[grey]Clocks[/]", CreateClockMarkup(currentGame, whiteTime, blackTime));
        grid.AddRow("[grey]Analysis[/]", analysis is null ? "[dim]-[/]" : $"[#d69e2e]{Escape(FormatAnalysis(analysis))}[/]");
        grid.AddRow("[grey]Search[/]", searchStatus is null ? "[dim]-[/]" : $"[white]{Escape(FormatSearchStatus(searchStatus))}[/]");

        return new Panel(grid)
        {
            Header = new PanelHeader($"[bold] LIVE GAME: {Escape(currentGame.GameId)} [/]"),
            Border = BoxBorder.Double,
            BorderStyle = new Style(PanelColor),
            Expand = true
        };
    }

    private static string CreateClockMarkup(GameStatus currentGame, int whiteTime, int blackTime)
    {
        var whiteColor = currentGame.ActiveColor is ChessColor.White ? "#d69e2e" : "white";
        var blackColor = currentGame.ActiveColor is ChessColor.Black ? "#d69e2e" : "white";

        return $"{Escape(currentGame.WhiteName)} [{whiteColor}]{FormatClock(whiteTime)}[/]   •   {Escape(currentGame.BlackName)} [{blackColor}]{FormatClock(blackTime)}[/]";
    }

    private void RequestRender()
    {
        if (Console.IsOutputRedirected is false && renderSignal.CurrentCount is 0)
        {
            renderSignal.Release();
        }
    }

    private void UpdateClocks(object? state)
    {
        lock (stateLock)
        {
            if (game is null)
            {
                return;
            }
        }

        RequestRender();
    }

    private static string Escape(string value) => Markup.Escape(value);

}
