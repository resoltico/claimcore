module ClaimCore.IntegrationTests.CsvExportHostTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Expecto
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Wire.Export
open ClaimCore.Json
open ClaimCore.Server
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | value -> failtestf "Expected authorized query: %A" value

let private render =
    // The host embeds the source catalog; this test consumes the same existing CSV presentation contract.
    use stream =
        typeof<RouteReply>.Assembly.GetManifestResourceStream("ClaimCore.Localization.en.core.json")
        |> nonNull

    use buffer = new IO.MemoryStream()
    stream.CopyTo buffer
    let entries, _ = LocalizationPolicy.load "en" [ buffer.ToArray() ] []

    let catalog =
        entries |> Map.toList |> Messages.catalog |> Result.defaultWith failtest

    Messages.render catalog

let private collect
    (header: byte array)
    (readPage: string option -> Task<'page>)
    (encode: 'page -> byte array)
    (next: 'page -> string option)
    =
    task {
        use bytes = new IO.MemoryStream()
        bytes.Write(header: byte array)
        let mutable cursor = None
        let mutable finished = false

        while not finished do
            let! page = readPage cursor
            bytes.Write(encode page)
            cursor <- next page
            finished <- cursor.IsNone

        return bytes.ToArray()
    }

let private qualify () =
    task {
        let principal =
            PrincipalKey.human TestServerFixture.oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (fun _ -> failtest "Synthetic principal")

        let! database = PostgresFixture.createFor principal

        try
            let fixture = database.Fixture
            let! service = caseWorker fixture

            let registration =
                DomainInputs.registrationFrom [ BusinessField.PolicyNumber, "=CSV α \"quoted\"" ]

            for index in 1..51 do
                let reference = sprintf "CSV-%03d" index

                let! opened =
                    Service.executeSubmission
                        service
                        (request
                            fixture
                            (10000 + index)
                            reference
                            0L
                            "case.open"
                            (RegistrationInput.values registration |> Submissions.fields))
                        token

                accepted opened |> ignore

            let! context = Service.context service () token

            let today =
                match (read context).Requester with
                | RequesterState.Active value -> value.Runtime.Basis.BusinessDate
                | value -> failtestf "%A" value

            let target = CaseSelector.Reference "CSV-001"

            for index in 1..51 do
                let input =
                    Submissions.create
                        "reminder.create"
                        (id fixture (20000 + index))
                        (Binding.ReminderNew target)
                        (Submissions.reminderPlan
                            (sprintf "+CSV α \"%d\"" index)
                            ((today.AddDays 1)
                                .ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)))

                let! reply = Service.executeSubmission service input token
                accepted reply |> ignore

            let! note =
                Service.executeSubmission
                    service
                    (Submissions.create
                        "note.add"
                        (id fixture 40000)
                        (Binding.NoteNew target)
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.NoteText,
                                SubmissionValue.Text "Quoted α\n=CSV \"note\""
                            ]))
                    token

            accepted note |> ignore

            for index in 1..51 do
                let key =
                    PrincipalKey.service
                        TestServerFixture.oidc.Issuer.AbsoluteUri
                        (sprintf "csv-service-%d" index)
                    |> Result.defaultWith (fun _ -> failtest "Principal")

                let values =
                    Map.fold
                        (fun values key value -> Map.add key value values)
                        (Submissions.principal key)
                        (Submissions.profile
                            {
                                ActorProfileInput.DisplayName = sprintf "=CSV α \"person %d\"" index
                                Contact = None
                            })

                let! reply =
                    Service.executeSubmission
                        service
                        (Submissions.create
                            "people.register"
                            (id fixture (30000 + index))
                            (Binding.PeopleNew Revision.zero)
                            values)
                        token

                acceptedPeople reply

            let mutable inject = false
            let mutable reads = 0
            let observed = ResizeArray<int64>()
            let mutable capacity: PrincipalAdmission option = None
            let mutable afterFirst: unit -> Task<unit> = fun () -> Task.FromResult()

            let decorate (store: IStore) =
                { new IStore with
                    member _.Context query = store.Context query
                    member _.Write operation = store.Write operation

                    member _.Read operation =
                        task {
                            let! result = store.Read operation

                            if inject then
                                reads <- reads + 1
                                observed.Add(capacity.Value.ActiveReads)

                                if reads = 1 then
                                    do! afterFirst ()

                            return result
                        }
                }

            use host =
                new TestServerFixture.Host(
                    true,
                    installationId = fixture.InstallationId,
                    forActor =
                        (fun principal -> core fixture principal |> Service.mapStore decorate)
                )

            capacity <- Some(host.Services.GetRequiredService<PrincipalAdmission>())
            let! _ = host.Login()
            let! credential = host.Token()

            let filter =
                { CaseListFilter.empty<string> with
                    Text = Some "CSV-"
                }

            let timeline =
                {
                    CaseTimelineRequest.Target = target
                    Filter = None
                    AfterCursor = None
                    Limit = Paging.MaximumPageSize
                }

            let reminders =
                {
                    ReminderListRequest.Target = Some target
                    View = None
                    State = Some(ReminderStateSelection.State ReminderState.Pending)
                    Due = None
                    Assignment = None
                    Text = None
                    AfterCursor = None
                    Limit = Paging.MaximumPageSize
                    IncludeInactive = None
                }

            let people =
                {
                    PeopleHistoryRequest.TargetActorId = None
                    AfterCursor = None
                    Limit = Paging.MaximumPageSize
                }

            let export selection expected =
                task {
                    use! response =
                        host.CallResponse(
                            ExportTransport.token,
                            Codec.encode ExportTransport.request selection,
                            credential
                        )

                    let! actual = response.Content.ReadAsByteArrayAsync()
                    Expect.equal (int response.StatusCode) 200 "Complete CSV success"

                    Expect.equal
                        (nonNull response.Content.Headers.ContentType).MediaType
                        "text/csv"
                        "CSV media writer"

                    Expect.equal
                        response.Content.Headers.ContentLength
                        (Nullable(int64 actual.Length))
                        "Exact complete encoded length"

                    Expect.equal
                        (response.Content.Headers.ContentEncoding |> Seq.toList)
                        [ "identity" ]
                        "Identity encoding"

                    Expect.stringContains
                        (string response.Headers.CacheControl)
                        "no-transform"
                        "No compression/transformation"

                    Expect.stringContains
                        (string response.Headers.CacheControl)
                        "no-store"
                        "Private response"

                    Expect.equal
                        (response.Headers.GetValues("ClaimCore-Installation") |> Seq.exactlyOne)
                        (fixture.InstallationId.ToString("D"))
                        "Authoritative installation echo"

                    Expect.equal
                        ((nonNull (nonNull response.Content.Headers.ContentDisposition).FileName)
                            .Trim('"'))
                        (ExportTransport.filename selection)
                        "Fixed safe filename"

                    Expect.equal
                        actual
                        expected
                        "Matches existing authorized Wire CSV bytes across every page"

                    return Encoding.UTF8.GetString actual
                }

            let! cases =
                collect
                    CaseCsv.csvHeader
                    (fun cursor ->
                        task {
                            let! result =
                                Service.list
                                    service
                                    {
                                        Filter = filter
                                        Page =
                                            {
                                                AfterCursor = cursor
                                                Limit = Paging.MaximumPageSize
                                            }
                                    }
                                    token in

                            return read result
                        })
                    (fun (page: CaseSummaryPage) -> CaseCsv.csvRows page.Basis page.Items)
                    _.NextCursor

            let! caseText = export (ExportSelection.Cases filter) cases
            Expect.stringContains caseText "'=CSV" "Spreadsheet formula neutralization in case rows"

            let! events =
                collect
                    TimelineCsv.csvHeader
                    (fun cursor ->
                        task {
                            let! result =
                                Service.timeline
                                    service
                                    { timeline with AfterCursor = cursor }
                                    token in

                            return read result
                        })
                    TimelineCsv.csvRows
                    _.NextCursor

            let! eventText = export (ExportSelection.Timeline timeline) events
            Expect.stringContains eventText "'+CSV" "Timeline formula neutralization"

            let! reminderBytes =
                collect
                    ReminderCsv.csvHeader
                    (fun cursor ->
                        task {
                            let! result =
                                Service.reminders
                                    service
                                    { reminders with AfterCursor = cursor }
                                    token in

                            return read result
                        })
                    (fun (page: ReminderPage) -> ReminderCsv.csvRows render page.Items)
                    _.NextCursor

            let! reminderText = export (ExportSelection.Reminders reminders) reminderBytes
            Expect.stringContains reminderText "'+CSV" "Reminder formula neutralization"

            let! peopleBytes =
                collect
                    PeopleHistoryCsv.csvHeader
                    (fun cursor ->
                        task {
                            let! result =
                                Service.peopleHistory
                                    service
                                    { people with AfterCursor = cursor }
                                    token in

                            return read result
                        })
                    (fun (page: PeopleHistoryPage) -> PeopleHistoryCsv.csvRows render page.Entries)
                    _.NextCursor

            let! peopleText = export (ExportSelection.PeopleHistory people) peopleBytes
            Expect.stringContains peopleText "'=CSV" "People history formula neutralization"

            let! empty =
                export
                    (ExportSelection.Cases
                        { filter with
                            Text = Some "no-such-csv-selection"
                        })
                    CaseCsv.csvHeader

            Expect.equal
                empty
                (Encoding.UTF8.GetString CaseCsv.csvHeader)
                "Authorized empty query is header-only"

            inject <- true
            reads <- 0
            observed.Clear()

            afterFirst <-
                fun () ->
                    task {
                        for ordinal, role in
                            [ 60001, Role.CaseHandler; 60002, Role.PaymentRecorder ] do
                            let! reply =
                                grant
                                    service
                                    (id fixture ordinal)
                                    principal
                                    {
                                        Role = role
                                        Scope = GrantTarget.Installation
                                    }
                                    false

                            acceptedPeople reply
                    }

            use! revoked =
                host.CallResponse(
                    ExportTransport.token,
                    Codec.encode ExportTransport.request (ExportSelection.Cases filter),
                    credential
                )

            let! revokedBytes = revoked.Content.ReadAsByteArrayAsync()

            Expect.equal
                (nonNull revoked.Content.Headers.ContentType).MediaType
                "application/json"
                "Page-two authorization refusal has no successful partial CSV"

            match Codec.decode Limits.MaximumResponseBytes ExportTransport.failure revokedBytes with
            | Ok(ExportFailure.Rejected _) -> ()
            | value -> failtestf "Expected current-rights refusal: %A" value

            Expect.equal reads 2 "Rights rechecked on each page"

            Expect.equal
                (List.ofSeq observed)
                [ 1L; 1L ]
                "One outer lease, no nested per-page HTTP admission"

            inject <- false

            for ordinal, role in [ 60003, Role.CaseHandler; 60004, Role.PaymentRecorder ] do
                let! reply =
                    grant
                        service
                        (id fixture ordinal)
                        principal
                        {
                            Role = role
                            Scope = GrantTarget.Installation
                        }
                        true

                acceptedPeople reply

            inject <- true
            reads <- 0
            observed.Clear()

            afterFirst <-
                fun () ->
                    task {
                        use! connection = database.OwnerSource.OpenConnectionAsync()

                        let! _ =
                            DatabaseSession.execute
                                connection
                                "ALTER TABLE claimcore.cases RENAME TO csv_cases_failure"
                                [||]
                                token

                        return ()
                    }

            try
                use! failed =
                    host.CallResponse(
                        ExportTransport.token,
                        Codec.encode ExportTransport.request (ExportSelection.Cases filter),
                        credential
                    )

                let! failedBytes = failed.Content.ReadAsByteArrayAsync()

                Expect.equal
                    (nonNull failed.Content.Headers.ContentType).MediaType
                    "application/json"
                    "Late provider failure emits no successful partial CSV"

                match
                    Codec.decode Limits.MaximumResponseBytes ExportTransport.failure failedBytes
                with
                | Ok(ExportFailure.Failed CoreFault.SchemaMismatch) -> ()
                | value -> failtestf "Expected actual late provider failure: %A" value
            finally
                inject <- false
                use connection = database.OwnerSource.OpenConnection()

                DatabaseSession.execute
                    connection
                    "ALTER TABLE claimcore.csv_cases_failure RENAME TO cases"
                    [||]
                    token
                |> _.GetAwaiter().GetResult()
                |> ignore

            Expect.equal reads 2 "Second core snapshot encountered the provider failure"

            Expect.equal
                (List.ofSeq observed)
                [ 1L; 1L ]
                "Cleanup preserves the single outer read lease"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync
        "real bounded CSV export host matches four multi-page formats and refuses late failures atomically"
        (async { do! qualify () |> Async.AwaitTask })
