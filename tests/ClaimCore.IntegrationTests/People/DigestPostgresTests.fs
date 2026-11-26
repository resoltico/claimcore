module ClaimCore.IntegrationTests.DigestPostgresTests

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Administration
open ClaimCore.Application.Verification
open ClaimCore.Server
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

let private recorded (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT actor_id,to_char(digest_date,'YYYY-MM-DD'),outcome FROM claimcore.digest_runs"
                [||]
                token

        return
            rows
            |> List.map (fun row ->
                DatabaseSession.value<Guid> 0 row,
                DatabaseSession.value<string> 1 row,
                DatabaseSession.optional<string> 2 row)
    }

let private withDigestCalendar work =
    async {
        let! database = PostgresFixture.createWithZone "Europe/Riga" |> Async.AwaitTask

        try
            do! work database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private assertDisabledHost
    (database: PostgresFixture.Database)
    runtime
    (digest: DigestConfiguration)
    smtpCounts
    =
    task {
        let assets = Directory.CreateTempSubdirectory "claimcore-disabled-digest-"

        try
            use certificate = ProbeHttpsFixture.certificate ()

            let configuration =
                { TestServerFixture.configuration certificate with
                    KeyRingDirectory = assets
                }

            let host configuration =
                let builder =
                    HostProgram.configuredBuilder
                        assets.FullName
                        configuration
                        TestServerFixture.oidc
                        runtime

                builder.WebHost.UseTestServer() |> ignore
                builder.Build()

            let hasDigest (host: IHost) =
                host.Services.GetServices<IHostedService>()
                |> Seq.exists (fun service -> service :? DigestService)

            use disabled = host configuration
            do! disabled.StartAsync token

            let lifetime = disabled.Services.GetRequiredService<IHostApplicationLifetime>()

            Expect.isTrue
                lifetime.ApplicationStarted.IsCancellationRequested
                "The host starts with digest settings absent"

            Expect.isFalse (hasDigest disabled) "Startup leaves the digest worker unregistered"

            let! claims = recorded database
            Expect.isEmpty claims "An otherwise eligible recipient has no digest claim"
            let! sends = smtpCounts ()
            Expect.equal sends (0L, 0L) "Mailpit records zero accepted and rejected deliveries"
            do! disabled.StopAsync token

            use configured =
                host
                    { configuration with
                        Digest = Some digest
                    }

            Expect.isTrue
                (hasDigest configured)
                "The same builder registers the worker when configured"
        finally
            assets.Delete(true)
    }

let private integration (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = populated database

        use mailpit =
            new MailpitFixture.Mailpit(DigestSmtpTls.StartTls, Some "@allowed[.]example[.]test$")

        do! mailpit.Start token

        let person operation label =
            task {
                let principal =
                    PrincipalKey.human "https://issuer.example/" label
                    |> Result.defaultWith (string >> failtest)

                let! submission =
                    personSubmission
                        fixture.Store
                        (id fixture operation)
                        principal
                        None
                        "people.register"
                        (Submissions.profile DomainInputs.actorProfile)

                let! result = Service.executeSubmission owner submission token
                acceptedPeople result
                return principal, id fixture operation
            }

        let profile operation target value =
            task {
                let values =
                    Submissions.profile DomainInputs.actorProfile
                    |> Map.add
                        (Submissions.input InputTarget.ProfileDigestEmail)
                        (SubmissionValue.Text value)

                let! submission =
                    personSubmission
                        fixture.Store
                        (id fixture operation)
                        target
                        None
                        "people.setProfile"
                        values

                let! result = Service.executeSubmission owner submission token
                acceptedPeople result
            }

        let! worker, workerId = person 901 "digest-worker"
        let! empty, emptyId = person 902 "digest-empty"
        let! disabled, _ = person 903 "digest-disabled"
        do! profile 904 worker "recipient@allowed.example.test"
        do! profile 905 empty "empty@allowed.example.test"
        do! profile 906 disabled "disabled@allowed.example.test"

        let! disable =
            personSubmission fixture.Store (id fixture 907) disabled None "people.disable" Map.empty

        let! result = Service.executeSubmission owner disable token
        acceptedPeople result

        let opening =
            request
                fixture
                908
                "DIGEST-HIDDEN-SYNTHETIC"
                0L
                "case.open"
                (Submissions.fields (RegistrationInput.values registration))

        let! opened = Service.executeSubmission owner opening token
        let hiddenId = accepted opened |> caseReceipt |> _.CaseId

        let! existing =
            Service.get
                owner
                {
                    Target = CaseSelector.Reference "synthetic-a"
                }
                token

        let visibleId =
            match existing with
            | Outcome.Succeeded value -> value.Snapshot.CaseId
            | _ -> failtest "Synthetic visible case expected."

        let fixtureCases =
            [
                911, visibleId, "DEADLINE", "Synthetic private note visible"
                914, hiddenId, "FOLLOW_UP", "Synthetic private note hidden"
            ]

        for operation, parent, kind, text in fixtureCases do
            let role =
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId parent
                }

            let! granted = grant owner (id fixture operation) worker role true
            acceptedPeople granted

            let selected = CaseSelector.Id parent

            let assign =
                Submissions.createRaw
                    "work.update"
                    (id fixture (operation + 1))
                    (OperationBinding.Work(selected, 0L))
                    (Submissions.work
                        {
                            Responsible = WorkValue.Set(workerId.ToString "D")
                            Summary = WorkValue.Keep
                        })

            let! assigned = Service.executeSubmission owner assign token
            accepted assigned |> ignore

            let values =
                Submissions.reminderPlan text "2026-11-17"
                |> Map.add
                    (Submissions.input InputTarget.ReminderPlanKind)
                    (SubmissionValue.Text kind)

            let reminder =
                Submissions.createRaw
                    "reminder.create"
                    (id fixture (operation + 2))
                    (OperationBinding.Reminder(selected, None))
                    values

            let! added = Service.executeSubmission owner reminder token
            accepted added |> ignore

        let! revoked =
            grant
                owner
                (id fixture 920)
                worker
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId hiddenId
                }
                false

        acceptedPeople revoked

        let! first =
            ClaimCore.Server.Composition.Runtime.openConnection
                (PostgresFixture.runtimeConnectionString database)
                database.MasterKey
                token

        let! second =
            ClaimCore.Server.Composition.Runtime.openConnection
                (PostgresFixture.runtimeConnectionString database)
                database.MasterKey
                token

        use first = first |> Result.defaultWith (string >> failtest)
        use second = second |> Result.defaultWith (string >> failtest)
        use log = new StringWriter()
        use sink = TextWriter.Synchronized log
        use metrics = new MetricObservations.Capture()

        let config =
            {
                DigestConfiguration.Smtp = mailpit.Smtp
                Time = TimeOnly(7, 0)
                Language = "en"
            }

        use http = new HttpClient(BaseAddress = mailpit.Api)

        let smtpCounts () =
            task {
                let! response = http.GetStringAsync("info", token)
                use info = JsonDocument.Parse response
                let stats = info.RootElement.GetProperty "RuntimeStats"

                return
                    stats.GetProperty("SMTPAccepted").GetInt64(),
                    stats.GetProperty("SMTPRejected").GetInt64()
            }

        do! assertDisabledHost database first config smtpCounts

        let origin = Uri "https://claims.example.test"
        let runFirst = DigestService.runDue sink origin first config
        let runSecond = DigestService.runDue sink origin second config

        let instant day hour minute =
            DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.Zero)

        do! runFirst (instant 18 4 55) token
        let! before = recorded database
        Expect.isEmpty before "06:55 local is before the configured digest time"

        let! _ =
            Task.WhenAll(runFirst (instant 18 5 0) token, runSecond (instant 18 5 0) token)

        let! rows = recorded database

        Expect.equal
            (Set.ofList rows)
            (set
                [
                    workerId, "2026-11-18", Some "SENT"
                    emptyId, "2026-11-18", Some "SKIPPED_EMPTY"
                ])
            "The two hosts send once, skip the empty queue and exclude disabled or opted-out people"

        let! response = http.GetStringAsync "messages"
        use messages = JsonDocument.Parse response
        let items = messages.RootElement.GetProperty "messages"
        Expect.equal (items.GetArrayLength()) 1 "One SMTP delivery across hosts"
        let idOfMessage = items[0].GetProperty("ID").GetString() |> nonNull
        let! message = http.GetStringAsync("message/" + idOfMessage)
        use mail = JsonDocument.Parse message

        Expect.equal
            (mail.RootElement.GetProperty("Subject").GetString())
            "Daily reminder digest (1)"
            "The inaccessible case contributes nothing"

        let body = mail.RootElement.GetProperty("Text").GetString() |> nonNull
        Expect.stringContains body "Overdue deadlines — 1" "Typed localized count"

        Expect.stringContains
            body
            "https://claims.example.test/reminders?view=MINE"
            "Reminders Mine link"

        for privateValue in
            [
                "Synthetic private note"
                "synthetic-a"
                "DIGEST-HIDDEN-SYNTHETIC"
                "Synthetic account"
                "1000"
                "Overdue follow-ups"
            ] do
            Expect.isFalse
                (body.Contains privateValue)
                "The digest discloses only authorized counts"

        do! profile 921 worker "denied@example.test"
        do! runFirst (instant 18 5 10) token
        let! beforeRefusal = smtpCounts ()
        Expect.equal beforeRefusal (1L, 0L) "The successful day had one delivery"

        do! runFirst (instant 19 5 0) token
        let! failedRows = recorded database

        Expect.contains
            failedRows
            (workerId, "2026-11-19", Some "FAILED")
            "SMTP refusal is recorded as FAILED"

        let! firstRefusal = smtpCounts ()
        Expect.equal firstRefusal (1L, 1L) "Mailpit counts one rejected delivery"

        do! runSecond (instant 19 5 10) token
        let! repeatedRows = recorded database

        Expect.equal
            (Set.ofList repeatedRows)
            (Set.ofList failedRows)
            "The other host adds no claim on the same date"

        let! repeatedRefusal = smtpCounts ()
        Expect.equal repeatedRefusal (1L, 1L) "Mailpit counts no same-day retry"

        do! runSecond (instant 20 5 0) token
        let! nextDay = smtpCounts ()
        Expect.equal nextDay (1L, 2L) "Mailpit counts a new attempt on the following business date"
        let! rows = recorded database

        Expect.equal
            (rows |> List.filter (fun (actor, _, _) -> actor = workerId) |> Set.ofList)
            (set
                [
                    workerId, "2026-11-18", Some "SENT"
                    workerId, "2026-11-19", Some "FAILED"
                    workerId, "2026-11-20", Some "FAILED"
                ])
            "Both rejected attempts have distinct business-date claims"

        let deliveryLog = log.ToString()
        Expect.stringContains deliveryLog (workerId.ToString "D") "Logs identify the actor"
        Expect.stringContains deliveryLog "FAILED" "Logs identify the outcome"
        Expect.isFalse (deliveryLog.Contains "example.test") "Logs exclude addresses"

        let outcomes =
            metrics.Values
            |> List.filter (fun sample -> sample.Instrument = "claimcore_digest_total")
            |> List.map _.Tags

        Expect.equal
            (Set.ofList outcomes)
            (set
                [
                    [ "outcome", "SENT" ]
                    [ "outcome", "SKIPPED_EMPTY" ]
                    [ "outcome", "FAILED" ]
                ])
            "Metric dimensions carry only the outcome"

        let! erase =
            personSubmission
                fixture.Store
                (id fixture 922)
                worker
                None
                "people.eraseProfile"
                Map.empty

        let! erased = Service.executeSubmission owner erase token
        acceptedPeople erased
        do! runFirst (instant 21 5 0) token
        let! rows = recorded database

        Expect.isFalse
            (rows
             |> List.exists (fun (actor, date, _) -> actor = workerId && date = "2026-11-21"))
            "Erasing the profile removes opt-in"

        let! _ = verified database

        let! checkpoint =
            LedgerAdministration.head database.OwnerSource database.MasterKey token

        do! enterMaintenance database

        let maintenance =
            MaintenanceHandle.create 1L (Guid.Parse "10000000-0000-4000-8000-000000000099")
            |> Option.get

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                checkpoint
                maintenance
                token

        Expect.isOk rebuilt "Operational digest claims cannot block rebuilding derived projections"
        let! afterRebuild = recorded database

        Expect.equal
            (Set.ofList afterRebuild)
            (Set.ofList rows)
            "Rebuild preserves attempts for the current day"

        let today = fixture.Calendar.DateAt DateTimeOffset.UtcNow
        let stale = today.AddDays(-31)

        do!
            sql
                database
                "INSERT INTO claimcore.digest_runs(actor_id,digest_date,claimed_at) VALUES($1,$2,clock_timestamp())"
                [| box workerId; box stale |]

        let! _ =
            Housekeeping.prune
                database.RuntimeSource
                database.Installation
                4
                fixture.Calendar.DateAt
                token

        let! afterPrune = recorded database

        Expect.isFalse
            (afterPrune
             |> List.exists (fun (_, date, _) ->
                 date =
                     stale.ToString(
                         "yyyy-MM-dd",
                         System.Globalization.CultureInfo.InvariantCulture
                     )))
            "Housekeeping removes expired claims"

        return ()
    }

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL daily digest uses local time, current rights and one SMTP attempt per day"
        (withDigestCalendar integration)
