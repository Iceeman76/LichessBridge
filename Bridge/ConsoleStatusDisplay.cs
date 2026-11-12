using System.Diagnostics;
using System.Globalization;
using DotNetLab.LichessBridge.Configuration;
using DotNetLab.LichessBridge.Domain;
using DotNetLab.LichessBridge.Engine;
using DotNetLab.LichessBridge.Game;
using DotNetLab.LichessBridge.Status;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using static DotNetLab.LichessBridge.Status.StatusFormatter;

namespace DotNetLab.Bridge;

public sealed class ConsoleStatusDisplay : IStatusDisplay, IDisposable
{
    private const int LogTimestampWidth = 8;
    private const int LogLevelWidth = 5;
    private const int LogCategoryWidth = 24;
    private const int LogLayoutOverhead = 13;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Color PanelColor = new(78, 92, 102);
    private static readonly Color AccentColor = new(214, 158, 46);
    private readonly ConsoleOutputPolicy outputPolicy;
    private readonly IAnsiConsole console;
    private readonly GameStatusFormatter gameFormatter = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly CoalescingRenderSignal renderSignal = new();
    private readonly Timer clockTimer;
    private readonly Task renderTask;
    private int isDisposed;
    private volatile StatusHeader? header;
    private volatile GameDisplayState? gameState;
    private volatile EngineAnalysis? analysis;
    private volatile EngineSearchStatus? searchStatus;
    private volatile string? hint;
    private volatile string status = "Starting...";
    private volatile bool quitRequested;
    private volatile ResignationRequestResult resignationResult;
    private volatile IReadOnlyList<DashboardLogEntry> logEntries = [];

    public ConsoleStatusDisplay(ConsoleOutputPolicy outputPolicy, IAnsiConsole? console = null)
    {
        this.outputPolicy = outputPolicy;
        this.console = console ?? AnsiConsole.Console;
        clockTimer = new Timer(UpdateClocks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        renderTask = Task.Run(() => this.console.Profile.Capabilities.Interactive ? RenderLiveAsync(stopping.Token) : RenderRedirectedAsync(stopping.Token));
    }

    public void ShowHeader(StatusHeader header)
    {
        this.header = header;

        RequestRender();
    }

    public void UpdateClock(GameClockStatus? clock)
    {
        outputPolicy.Update(clock);

        RequestRender();
    }

    public void ShowGame(GameStatus game)
    {
        gameState = new GameDisplayState(game, Stopwatch.GetTimestamp());

        RequestRender();
    }

    public void ShowAnalysis(EngineAnalysis? analysis)
    {
        this.analysis = analysis;

        RequestRender();
    }

    public void ShowSearchStatus(EngineSearchStatus? searchStatus)
    {
        this.searchStatus = searchStatus;

        RequestRender();
    }

    public void ShowHint(string? hint)
    {
        this.hint = hint;

        RequestRender();
    }

    public void ClearGame()
    {
        outputPolicy.Update(null);
        gameState = null;
        analysis = null;
        searchStatus = null;
        hint = null;
        resignationResult = ResignationRequestResult.NoActiveGame;

        RequestRender();
    }

    public void Show(string status)
    {
        if (this.status == status)
        {
            return;
        }

        this.status = status;

        RequestRender();
    }

    public void ShowQuit(bool requested)
    {
        quitRequested = requested;

        RequestRender();
    }

    public void ShowResign(ResignationRequestResult result)
    {
        resignationResult = result;

        RequestRender();
    }

    public void ShowLogs(IReadOnlyList<DashboardLogEntry> entries)
    {
        logEntries = entries;

        RequestRender();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) is 1)
        {
            return;
        }

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
            while (outputPolicy.IsQuiet)
            {
                await renderSignal.WaitAsync(cancellationToken);
            }

            await console.Live(CreateDashboard())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async context =>
            {
                var lastRefresh = Stopwatch.GetTimestamp();
                try
                {
                    while (cancellationToken.IsCancellationRequested is false)
                    {
                        await renderSignal.WaitAsync(cancellationToken);

                        var remaining = RefreshInterval - Stopwatch.GetElapsedTime(lastRefresh);

                        if (remaining > TimeSpan.Zero)
                        {
                            await Task.Delay(remaining, cancellationToken);
                        }

                        while (renderSignal.TryConsume())
                        {
                        }

                        if (outputPolicy.IsQuiet)
                        {
                            continue;
                        }

                        var dashboard = CreateDashboard();

                        if (outputPolicy.IsQuiet is false)
                        {
                            context.UpdateTarget(dashboard);
                            context.Refresh();
                            lastRefresh = Stopwatch.GetTimestamp();
                        }
                    }
                }
                finally
                {
                    if (outputPolicy.IsQuiet is false)
                    {
                        context.UpdateTarget(CreateDashboard());
                        context.Refresh();
                    }
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RenderRedirectedAsync(CancellationToken cancellationToken)
    {
        var lastStatus = string.Empty;
        try
        {
            while (cancellationToken.IsCancellationRequested is false)
            {
                await renderSignal.WaitAsync(cancellationToken);

                if (outputPolicy.IsQuiet || lastStatus == status)
                {
                    continue;
                }

                lastStatus = status;
                console.WriteLine(lastStatus);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private IRenderable CreateDashboard()
    {
        var currentHeader = header;
        var currentGame = gameState;
        var sections = new List<IRenderable>
        {
            currentHeader is null ? CreateEmptyHeaderPanel() : CreateHeaderPanel(currentHeader),
            currentGame is null ? CreateEmptyGamePanel() : CreateGamePanel(currentGame),
            CreateStatusPanel(),
            CreateLogPanel()
        };

        return new Rows(sections);
    }

    private Panel CreateLogPanel()
    {
        const int reservedRows = 32;
        var panelHeight = Math.Max(3, console.Profile.Height - reservedRows);
        var maximumEntries = panelHeight - 2;
        var messageWidth = Math.Max(20, console.Profile.Width - LogTimestampWidth - LogLevelWidth - LogCategoryWidth - LogLayoutOverhead);
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
            var normalizedMessage = ConsoleLogFormatter.Format(message).ReplaceLineEndings(" ").Replace("\t", "  ", StringComparison.Ordinal);
            var timestamp = entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture).PadRight(LogTimestampWidth);
            var fixedCategory = Truncate(category, LogCategoryWidth).PadRight(LogCategoryWidth);
            var fixedLevel = $"{level}{new string(' ', LogLevelWidth - 3)}";
            var formattedMessage = Escape(Truncate(normalizedMessage, messageWidth));

            if (entry.EventId == EngineLogEvents.UciInfoString && entry.Level < LogLevel.Warning)
            {
                formattedMessage = $"[cyan]{formattedMessage}[/]";
            }

            var line = $"{Escape(timestamp)}  {fixedLevel}  {Escape(fixedCategory)}  {formattedMessage}";
            lines.Add(new Markup(line));
        }

        if (lines.Count == 0)
        {
            lines.Add(new Text(string.Empty));
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
        var quit = quitRequested ? "✓ Quit" : string.Empty;
        var resign = resignationResult switch
        {
            ResignationRequestResult.ConfirmationRequested => "Press R again to continue resigning",
            ResignationRequestResult.FinalConfirmationRequested => "Resign game? Y = yes, any other key = cancel",
            _ => string.Empty
        };
        var actions = string.Join("   ", new[] { quit, resign }.Where(value => string.IsNullOrEmpty(value) is false));
        var currentStatus = string.IsNullOrWhiteSpace(hint) || status.StartsWith("Waiting for", StringComparison.OrdinalIgnoreCase) is false ? status : $"{status} Pondering on {hint}...";
        var maximumStatusWidth = Math.Max(1, console.Profile.Width - actions.Length - 8);
        currentStatus = Truncate(currentStatus.ReplaceLineEndings(" "), maximumStatusWidth);
        table.AddColumn(new TableColumn(string.Empty).NoWrap());
        table.AddColumn(new TableColumn(string.Empty).RightAligned().NoWrap());
        table.AddRow(new Markup($"[bold #d69e2e]{Markup.Escape(currentStatus)}[/]"), new Text(actions));

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
        engine.AddRow("[grey]Protocol[/]", currentHeader.Protocol is EngineProtocol.Uci ? "[bold]UCI[/]" : "[bold]WinBoard[/]");
        engine.AddRow("[grey]Pondering[/]", currentHeader.Ponder ? "[bold green]ON[/]" : "[bold red]OFF[/]");
        engine.AddRow("[grey]Auto challenge[/]", currentHeader.AutoBotChallenge ? "[bold green]ON[/]" : "[bold red]OFF[/]");

        if (currentHeader.Ratings.Count > 0)
        {
            engine.AddEmptyRow();
        }

        foreach (var rating in currentHeader.Ratings)
        {
            var games = rating.Games is { } gameCount ? $" - {gameCount} games" : string.Empty;
            engine.AddRow($"[grey]{Escape(FormatSpeed(rating.Speed))}[/]", $"[bold]{rating.Rating}{games}[/]");
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

    private static Panel CreateEmptyHeaderPanel()
    {
        var engine = new Grid();
        engine.AddColumn();
        engine.AddColumn();
        engine.AddRow("[grey]Engine[/]", string.Empty);
        engine.AddRow("[grey]Protocol[/]", string.Empty);
        engine.AddRow("[grey]Pondering[/]", string.Empty);
        engine.AddRow("[grey]Auto challenge[/]", string.Empty);
        engine.AddEmptyRow();
        engine.AddRow("[grey]Bullet[/]", string.Empty);
        engine.AddRow("[grey]Blitz[/]", string.Empty);
        engine.AddRow("[grey]Rapid[/]", string.Empty);

        var filters = new Grid();
        filters.AddColumn();
        filters.AddColumn();
        filters.AddRow("[grey]CHALLENGES[/]", string.Empty);
        filters.AddRow("[grey]Mode[/]", string.Empty);
        filters.AddRow("[grey]Variant[/]", string.Empty);
        filters.AddRow("[grey]Speed[/]", string.Empty);
        filters.AddEmptyRow();
        filters.AddRow("[grey]OPPONENTS[/]", string.Empty);
        filters.AddRow("[grey]Type[/]", string.Empty);

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

    private Panel CreateGamePanel(GameDisplayState state)
    {
        var currentGame = gameFormatter.Format(state.Game);
        var currentAnalysis = analysis;
        var currentSearchStatus = searchStatus;
        var opponentName = string.Equals(currentGame.WhiteName, header?.AccountName, StringComparison.OrdinalIgnoreCase) ? currentGame.BlackName : currentGame.WhiteName;
        var elapsed = Stopwatch.GetElapsedTime(state.UpdatedAt);
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
        grid.AddRow("[grey]Analysis[/]", currentAnalysis is null ? "[dim]-[/]" : $"[#d69e2e]{Escape(FormatAnalysis(currentAnalysis))}[/]");
        grid.AddRow("[grey]Search[/]", currentSearchStatus is null ? "[dim]-[/]" : $"[white]{Escape(FormatSearchStatus(currentSearchStatus))}[/]");

        return new Panel(grid)
        {
            Header = new PanelHeader($"[bold] LIVE GAME: {Escape(opponentName)} [/]"),
            Border = BoxBorder.Double,
            BorderStyle = new Style(PanelColor),
            Expand = true
        };
    }

    private static Panel CreateEmptyGamePanel()
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[grey]Game[/]", string.Empty);
        grid.AddRow("[white]White[/]", string.Empty);
        grid.AddRow("[grey]Black[/]", string.Empty);
        grid.AddRow("[grey]Last move[/]", string.Empty);
        grid.AddRow("[grey]Clocks[/]", string.Empty);
        grid.AddRow("[grey]Analysis[/]", string.Empty);
        grid.AddRow("[grey]Search[/]", string.Empty);

        return new Panel(grid)
        {
            Header = new PanelHeader("[bold] LIVE GAME [/]"),
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
        if (outputPolicy.IsQuiet is false)
        {
            renderSignal.Request();
        }
    }

    private void UpdateClocks(object? state)
    {
        if (gameState is null)
        {
            return;
        }

        RequestRender();
    }

    private sealed record GameDisplayState(GameStatus Game, long UpdatedAt);

    private static string Escape(string value) => Markup.Escape(value);

}
