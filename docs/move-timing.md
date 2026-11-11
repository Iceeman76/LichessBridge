# Tidsloggning av motorsvar

`MoveTiming` skrivs på Information efter varje dragförsök. Sök på `GameId` och därefter `CorrelationId` för att koppla ihop sammanfattningen, transportdetaljer och `ServerClock`. `Ply` är dragets absoluta halvdragsnummer, med första vitsvaret som 1. `Outcome=http-accepted` betyder att API-anropet lyckades; avbrutna och misslyckade försök får egna utfall. Mätpunkter som inte nåddes visas som `NaN` eller saknat värde.

Alla varaktigheter använder `Stopwatch.GetTimestamp()` och `Stopwatch.GetElapsedTime()`. UTC-fälten används för korrelation, inte för att beräkna varaktigheter. Filloggen bevarar UTC och decimaler även inne i meddelandet.

| Fält | Betydelse |
| --- | --- |
| `LocalTotalMs` | Från färdigläst NDJSON-rad till återkomst från draganropet, inklusive mottaget HTTP-svar. Vid fel: till att försöket avbryts. |
| `EventHandlingMs` | JSON/protokollhantering, eventuell kötid, statusuppdateringar och annan hantering före positionssättning. Vid gameFull ingår även startåtgärder som ratings och eventuell chatt. |
| `SetPositionMs` | Hela `SetPositionAsync`, inklusive replay, `exit`, `force` och andra positionskommandon när de behövs. |
| `EngineSetupMs` | Från färdig positionssättning till början av svarsväntan; bland annat `time`, `otim`, motståndardrag, eventuell `go` och statusförfrågan. |
| `ResponseWaitMs` | Från början av svarsväntan tills dragraden konsumeras i `GetMoveAsync` via `ReceiveMoveAsync`. Inkluderar lokal schemaläggning och överlämning. |
| `StdoutToGetMoveMs` | Från faktisk läsning av dragraden till konsumenten ovan. Kan överlappa `EngineSetupMs` om motorn svarar innan alla startkommandon har behandlats. |
| `EnginePostResponseMs` | Konvertering av draget samt efterarbete i motorn, inklusive eventuell `hint`, `.` och `force`, tills kontrollern får draget. |
| `ControllerPostResponseMs` | Från återkomst till kontrollern till anrop av `PlayMoveAsync`, inklusive registrering för klockkorrelation. |
| `SubmissionMs` | Hela `PlayMoveAsync`, inklusive API-lås, rate limit-väntan, eventuella omförsök och HTTP. |
| `HttpMs` | Summerad tid inne i `HttpClient.SendAsync`, inklusive läsning av svarsinnehållet. Är inte en separat fas att lägga ovanpå `SubmissionMs`. |
| `HttpQueueMs`, `HttpAttempts` | Väntan på API-lås/rate limit respektive antal HTTP-försök. |
| `CommandLockMs`, `CommandWriteMs`, `CommandFlushMs` | Summeringar för motorkommandon som anropas av dragförsöket. Ingår redan i ovanstående faser. |
| `EngineMoveFormat`, `MoveConversionMs` | Om motorn svarade med UCI eller SAN, samt lokal tid för tolkning och eventuell SAN-konvertering. Ingår i `EnginePostResponseMs`. |

De sju faserna från `EventHandlingMs` till `SubmissionMs` summerar till `LocalTotalMs` för ett lyckat drag. Lägg inte till delmätningarna igen. `EventUtc`, `StdoutUtc`, `GetMoveUtc`, `ControllerUtc` och `CompletedUtc` visar separata observationspunkter. Stdout läses före protokolloggning och callbacks, och dragraden lämnas till svarskön först.

Mottagningen av spelströmmen körs separat från draghanteringen så att nästa uppdatering kan tidsstämplas även medan HTTP-anropet pågår. Tidsstämpeln avser när en komplett rad läses av applikationen, inte paketets ankomst till nätverkskortet. Eventkön rymmer 256 uppdateringar och använder backpressure utan att kasta bort spelhistorik.

`PonderEnabled`, `WasPondering`, `SearchPath=continue-pondering|new-search` och `LeftAnalysis` skiljer fortsättning av pondering från ny sökning och utträde ur analysläge. `Commands` visar kommandona även när full protokolloggning är avstängd.

Visningen av senaste draget och SAN-tolkningen återanvänder nu brädställningen mellan uppdateringar. Bara nya drag spelas igenom; dubletter återanvänder resultatet. En första lång snapshot eller ändrad historik kräver fortfarande att ställningen byggs upp. Se [mätning av SAN-optimeringen](san-position-cache.md).

## Serverklockor

`WhiteMs`, `BlackMs`, `WhiteIncrementMs` och `BlackIncrementMs` återger servervärdena. Inkrement hämtas från `winc`/`binc` när de finns, annars från gameFull-klockan.

När en efterföljande uppdatering bekräftar rätt drag beräknas:

```text
ServerConsumedMs = BeforeMs + IncrementMs - AfterMs
```

Spelsessionen behåller väntande korrelationer över återanslutningar och motoromstarter. Matchningen kontrollerar hela dragprefixet, inte bara sista draget. En uppdatering efter motståndarens följande drag kan fortfarande användas eftersom vår rapporterade klocka då är densamma. När ännu ett eget drag passerat, eller historiken skiljer sig, loggas förbrukningen som okänd. Dubletter ger ingen andra mätning. Ett omförsök på samma ply behåller det första försökets korrelation och basvärde. Negativa differenser bevaras för att synliggöra eventuella serverjusteringar.

`UpdateUtc` är vår mottagningstid för uppdateringen. Varken denna tid eller HTTP-svarets ankomst anger exakt när Lichess registrerade draget. Skillnaden mellan `ServerConsumedMs` och lokala tider är därför **inte exakt nätlag**. Den kan omfatta olika mätgränser, tid före vår radläsning, serverhantering, klockkompensation och transporttid.

## Transport och overhead

Sätt `Logging:LogLevel:DotNetLab.LichessBridge.Engine.WinBoardEngine` till `Debug` för detaljer. `EngineTransport` visar varje kommando med UTC, korrelation samt väntan på låset, skrivning och flush separat. Detta inkluderar periodiska `.`; de kopplas till motorns senast aktiva dragkorrelation och kan komma efter att dragets sammanfattning skrivits. `EngineCommand` återger dragets egna mätningar efter HTTP-anropet. Båda är samma mätningar och ska inte summeras tillsammans.

Motorns stdin har `AutoFlush=false`; varje kommando följs av en explicit uppmätt `FlushAsync`. Skrivning och flush är därmed separata steg. Transportlåset släpps före Debug-loggningen. `EngineOutput` visar `LoggingMs` och `CallbackMs`. Befintliga analys-, hint- och statuscallbacks är synkrona och uppdaterar dashboardens tillstånd och renderingssignal; själva renderingen körs separat. En lång callback på en tidigare rad kan fortfarande fördröja läsning av nästa stdout-rad och syns i dessa Debug-mätningar. Stdout-tiden är inte tidpunkten då motorn producerade texten, och Bridge mäter inte själva TB-proben.

Filloggningen använder en separat bakgrundsskrivare med buffrad skrivning och flush per batch. Dragtråden gör ingen fil-I/O eller fil-flush. Kön är begränsad till 8192 rader och blockerar inte draget; överbelastning rapporteras som tappade loggrader i filen. Normal avstängning tömmer kön. Vid abrupt processavslut kan de sista buffrade raderna saknas. Meddelandeformatering och dashboardloggning är fortfarande synkrona, så använd Information för löpande mätning och Debug för riktad felsökning. Inga nya loggfält innehåller token eller HTTP-auktoriseringshuvuden.

## Exempel: ett lokalt drag på 50 ms

Detta är ett illustrativt, förkortat exempel, inte en rekonstruktion av KSlDhaeH:

```text
MoveTiming GameId=example Ply=394 Move=a7a8 CorrelationId=abc Outcome=http-accepted LocalTotalMs=50.000 EventHandlingMs=2.000 SetPositionMs=1.000 EngineSetupMs=2.000 ResponseWaitMs=9.000 StdoutToGetMoveMs=0.200 EnginePostResponseMs=1.000 ControllerPostResponseMs=0.100 SubmissionMs=34.900 HttpMs=34.800 HttpQueueMs=0.050 HttpAttempts=1 Commands=time 120;otim 140;a1b1;hint;. PonderEnabled=True WasPondering=True SearchPath=continue-pondering LeftAnalysis=False
ServerClock GameId=example Ply=394 Move=a7a8 CorrelationId=abc ObservedPly=394 ServerConsumedMs=130 BeforeMs=1200 AfterMs=1070 IncrementMs=0
```

Här tar svarsväntan 9 ms, motorstart och efterarbete 3 ms, eventhantering och positionssättning 3 ms och inlämningen cirka 35 ms. Ett snabbt motorsvar kan alltså ge 50 ms lokal totaltid, främst på grund av HTTP. Servern rapporterar samtidigt 130 ms förbrukning. De återstående 80 ms kan inte förklaras exakt av Bridge-mätningen och ska inte benämnas nätlag.

## Lokal verifiering

```powershell
dotnet build LichessBridge.slnx --no-restore
dotnet test LichessBridge.Tests/LichessBridge.Tests.csproj --no-restore --logger "trx;LogFileName=timing-verification.trx"
dotnet test LichessBridge.Tests/LichessBridge.Tests.csproj --no-build --filter "FullyQualifiedName~MoveTimingTests" --logger "console;verbosity=normal"
```

`MoveTimingTests` startar projektets lokala WinBoard-testmotor och använder en simulerad HTTP-handler med väntan på dragsvaret. Inget riktigt Lichess-parti startas och inga nätverksanrop görs. Testerna kontrollerar sammanhängande faser, tidsstämplar, HTTP, korrelation, kommandon utan protokolloggning, pondering och analysläge. `ServerClockTrackerTests` kontrollerar inkrement, båda färgerna, dubletter, återanslutningssnapshots, saknade uppdateringar och separata spel. `LichessClientTests` kontrollerar tidsstämpling före loggning och fortsatt mottagning medan konsumenten är upptagen.

Verifierat 2026-11-11: 160 av 160 NUnit-tester passerade. Hela lösningen byggdes utan varningar eller fel. TRX-resultat finns i `LichessBridge.Tests/TestResults/timing-verification.trx` efter ovanstående körning. Testmotorn mäter transportflödet; verifieringen gör inget prestandapåstående om Alarm.exe eller dess TB-prober.
