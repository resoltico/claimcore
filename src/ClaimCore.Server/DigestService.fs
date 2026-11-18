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
        let english = CatalogRoutes.core LocalizationPolicy.defaultLanguage

        let entries, _ =
            LocalizationPolicy.load language [ english ] [ CatalogRoutes.core language ]

        let catalog = Messages.catalog (Map.toList entries) |> Result.defaultWith invalidOp
        let link = Uri(origin, "/reminders?view=" + ReminderView.token ReminderView.Mine)

        let send recipient counts ct =
            task {
                let subject, body = DigestMessage.format catalog language link counts

                try
                    do! DigestSmtp.send config.Smtp (EmailAddress.format recipient) subject body ct
                    return true
                with
                | :? OperationCanceledException when ct.IsCancellationRequested ->
                    return raise (OperationCanceledException(ct))
                // SMTP replies and transport exceptions may contain addresses or server responses.
                | _ -> return false
            }

        fun (instant: DateTimeOffset) token ->
            let local = TimeZoneInfo.ConvertTime(instant, zone).DateTime

            if ReminderDigest.scheduled config.Time (TimeOnly.FromDateTime local) then
                let basis =
                    {
                        WorkDateBasis.ObservedAt = instant
                        BusinessDate = DateOnly.FromDateTime local
                        TimeZoneId = zone.Id
                    }

                runtime.RunDigests(basis, send, log sink, token)
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
