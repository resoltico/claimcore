namespace ClaimCore.Server

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Presentation
open ClaimCore.Server.Composition
open Microsoft.Extensions.Hosting

module internal DigestService =
    let private log (sink: TextWriter) actor outcome =
        let category = DigestOutcome.token outcome

        let line =
            JsonSerializer.Serialize
                {|
                    kind = "digest"
                    actorId = ActorId.value actor
                    outcome = category
                |}

        try
            sink.WriteLine line
        with _ ->
            ()

        WebMetrics.digest outcome

    let runDue (sink: TextWriter) (origin: Uri) (runtime: IRuntime) (config: DigestConfiguration) =
        let zone = TimeZoneInfo.FindSystemTimeZoneById runtime.BusinessTimeZone
        let language = config.Language
        let english = NativeCatalog.core LocalizationPolicy.defaultLanguage

        let entries, _ =
            LocalizationPolicy.load language [ english ] [ NativeCatalog.core language ]

        let catalog = Messages.catalog (Map.toList entries) |> Result.defaultWith invalidOp
        let link = Uri(origin, "/reminders?view=" + ReminderView.token ReminderView.Mine)

        let send recipient counts ct =
            let subject, body = DigestMessage.format catalog language link counts
            DigestSmtp.send config.Smtp (EmailAddress.format recipient) subject body ct

        let observe (summary: DigestRunSummary) =
            let outcomes =
                summary.Terminal
                |> Map.toSeq
                |> Seq.map (fun (outcome, count) -> DigestOutcome.token outcome, count)
                |> dict

            try
                sink.WriteLine(
                    JsonSerializer.Serialize(
                        {|
                            kind = "digest_runs"
                            inFlight = summary.InFlight
                            expired = summary.Expired
                            outcomes = outcomes
                        |}
                    )
                )
            with _ ->
                ()

        fun (instant: DateTimeOffset) token ->
            let local = TimeZoneInfo.ConvertTime(instant, zone).DateTime

            if ReminderDigest.scheduled config.Time (TimeOnly.FromDateTime local) then
                let basis =
                    {
                        WorkDateBasis.ObservedAt = instant
                        BusinessDate = DateOnly.FromDateTime local
                        TimeZoneId = zone.Id
                    }

                task {
                    let! summary = runtime.RunDigests(basis, send, log sink, token)
                    observe summary
                }
            else
                Task.FromResult()

type internal DigestService
    (work: DateTimeOffset -> CancellationToken -> Task<unit>, clock: TimeProvider) =
    inherit BackgroundService()

    override _.ExecuteAsync token =
        task {
            try
                while not token.IsCancellationRequested do
                    let delay = TimeSpan.FromSeconds(float (300 + Random.Shared.Next(-30, 31)))
                    do! Task.Delay(delay, clock, token)

                    try
                        do! work (clock.GetUtcNow()) token
                    with
                    | :? OperationCanceledException when token.IsCancellationRequested -> ()
                    | error -> RequestLog.reportException Console.Error error
            with :? OperationCanceledException when token.IsCancellationRequested ->
                ()
        }
