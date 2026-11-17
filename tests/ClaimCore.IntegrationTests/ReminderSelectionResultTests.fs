module ClaimCore.IntegrationTests.ReminderSelectionResultTests

open System
open System.IO
open System.Text
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = Threading.CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected authorized reminder results: %A" other

let private required result =
    Result.defaultWith (string >> failtest) result

let private wire text =
    Codec.decode
        Limits.MaximumRequestBytes
        Api.reminders.Input
        (Encoding.UTF8.GetBytes(text: string))
    |> required

let private rows (page: ReminderPage) =
    page.Items
    |> List.map (fun row ->
        ReminderId.value (Reminder.id row.Reminder),
        ReminderState.token (Reminder.state row.Reminder))

let private csvRows bytes =
    use text = new StringReader(Encoding.UTF8.GetString(bytes: byte array))
    use parser = new TextFieldParser(text)
    parser.SetDelimiters [| "," |]
    parser.HasFieldsEnclosedInQuotes <- true
    let headers = parser.ReadFields() |> nonNull |> Array.toList
    let result = ResizeArray<_>()

    while not parser.EndOfData do
        result.Add(List.zip headers (parser.ReadFields() |> nonNull |> Array.toList) |> Map.ofList)

    List.ofSeq result

let private run action =
    async {
        let principal =
            PrincipalKey.human TestServerFixture.oidc.Issuer.AbsoluteUri "synthetic-human"
            |> required

        let! database = PostgresFixture.createFor principal |> Async.AwaitTask

        try
            let fixture = database.Fixture
            let! service = caseWorker fixture |> Async.AwaitTask

            let! steward =
                grant
                    service
                    (id fixture 3)
                    fixture.Owner
                    {
                        Role = Role.DataSteward
                        Scope = GrantTarget.Installation
                    }
                    true
                |> Async.AwaitTask

            acceptedPeople steward

            let execute number taskName binding values =
                task {
                    let! result =
                        Service.executeSubmission
                            service
                            (Submissions.create taskName (id fixture number) binding values)
                            token

                    accepted result |> ignore
                }

            for number, reference in [ 10, "STATE-OPEN"; 11, "STATE-CLOSED"; 12, "STATE-VOID" ] do
                let! reply =
                    Service.executeSubmission
                        service
                        (request
                            fixture
                            number
                            reference
                            0L
                            "case.open"
                            (Submissions.fields (RegistrationInput.values registration)))
                        token
                    |> Async.AwaitTask

                accepted reply |> ignore

            for number, reference, due in
                [
                    101, "STATE-OPEN", "2026-01-01"
                    102, "STATE-OPEN", "2026-01-02"
                    103, "STATE-OPEN", "2026-01-03"
                    104, "STATE-CLOSED", "2026-01-04"
                    105, "STATE-VOID", "2026-01-05"
                ] do
                do!
                    execute
                        number
                        "reminder.create"
                        (Binding.ReminderNew(CaseSelector.Reference reference))
                        (Submissions.reminderPlan (sprintf "Literal reminder %d" number) due)
                    |> Async.AwaitTask

            for number, target, reminder, taskName in
                [
                    202, "STATE-OPEN", 102, "reminder.complete"
                    203, "STATE-OPEN", 103, "reminder.cancel"
                    204, "STATE-CLOSED", 104, "reminder.complete"
                    205, "STATE-VOID", 105, "reminder.complete"
                ] do
                do!
                    execute
                        number
                        taskName
                        (Binding.Reminder(
                            CaseSelector.Reference target,
                            ReminderId.create (id fixture reminder) |> Option.get,
                            Revision.first
                        ))
                        Map.empty
                    |> Async.AwaitTask

            do!
                execute
                    301
                    "case.close"
                    (Binding.Case(CaseSelector.Reference "STATE-CLOSED", Revision.first))
                    Map.empty
                |> Async.AwaitTask

            do!
                execute
                    302
                    "case.void"
                    (Binding.Case(CaseSelector.Reference "STATE-VOID", Revision.first))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.Reason,
                            SubmissionValue.Text "Synthetic duplicate"
                        ])
                |> Async.AwaitTask

            do! action database service |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private expectRows (database: PostgresFixture.Database) expected page =
    Expect.equal
        (rows page)
        (expected |> List.map (fun (number, state) -> id database.Fixture number, state))
        "Literal accepted reminder identities and stored states"

let private coreLink (database: PostgresFixture.Database) service =
    task {
        let query =
            match Link.remindersForCase (CaseSelector.Reference "STATE-OPEN") with
            | Link.ReminderList request -> request
            | _ -> failtest "Reminder list link required"

        let! actual = Service.reminders service query token
        expectRows database [ 101, "PENDING"; 102, "COMPLETED"; 103, "CANCELLED" ] (read actual)

        let! omitted =
            Service.reminders
                service
                { query with
                    State = None
                    View = None
                    IncludeInactive = None
                }
                token

        expectRows database [ 101, "PENDING" ] (read omitted)
    }

let private pagingAndRights (database: PostgresFixture.Database) service =
    task {
        let query =
            wire
                """{"target":{"kind":"REFERENCE","caseReference":"STATE-OPEN"},"state":"ANY","includeInactive":true,"limit":1}"""

        let! first = Service.reminders service query token
        let first = read first
        expectRows database [ 101, "PENDING" ] first
        Expect.isSome first.NextCursor "Other states remain on later pages"

        let! second =
            Service.reminders
                service
                { query with
                    AfterCursor = first.NextCursor
                }
                token

        let second = read second
        expectRows database [ 102, "COMPLETED" ] second
        Expect.isSome second.NextCursor "Cancelled row remains"

        let! third =
            Service.reminders
                service
                { query with
                    AfterCursor = second.NextCursor
                }
                token

        let third = read third
        expectRows database [ 103, "CANCELLED" ] third
        Expect.isNone third.NextCursor "Exactly three selected rows"

        let! invalid =
            Service.reminders
                service
                { query with
                    AfterCursor = first.NextCursor
                    IncludeInactive = Some false
                }
                token

        Expect.equal
            invalid
            (Outcome.Rejected Refusal.InvalidCursor)
            "Explicit false cannot reuse a true-bound cursor"

        let! counts = Service.workViews service () token

        let counts =
            (read counts).ReminderViews
            |> List.map (fun value -> value.View, value.Count)
            |> Map.ofList

        Expect.equal
            counts[ReminderView.Pending]
            1L
            "Default pending view counts only the pending row"

        Expect.equal
            counts[ReminderView.Completed]
            3L
            "Completed view includes independently inactive parents by default"

        Expect.equal counts[ReminderView.Cancelled] 1L "Cancelled view remains independent of ANY"

        let scoped =
            PrincipalKey.human TestServerFixture.oidc.Issuer.AbsoluteUri "scoped-reminder-reader"
            |> required

        let values =
            Map.fold
                (fun result key value -> Map.add key value result)
                (Submissions.principal scoped)
                (Submissions.profile
                    {
                        DisplayName = "Scoped reader"
                        Contact = None
                    })

        let! registered =
            Service.executeSubmission
                service
                (Submissions.create
                    "people.register"
                    (id database.Fixture 401)
                    (Binding.PeopleNew Revision.zero)
                    values)
                token

        acceptedPeople registered

        let! granted =
            grant
                service
                (id database.Fixture 402)
                scoped
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId(CaseId.create (id database.Fixture 10) |> Option.get)
                }
                true

        acceptedPeople granted

        let! scopedRows =
            Service.reminders
                (core database.Fixture scoped)
                (wire """{"state":"ANY","includeInactive":true,"limit":50}""")
                token

        expectRows database [ 101, "PENDING"; 102, "COMPLETED"; 103, "CANCELLED" ] (read scopedRows)
    }

let private inactiveAndExport (database: PostgresFixture.Database) service =
    task {
        let completed inactive =
            wire ("{\"view\":\"COMPLETED\",\"limit\":50" + inactive + "}")

        let! restricted =
            Service.reminders service (completed ",\"includeInactive\":false") token

        expectRows database [ 102, "COMPLETED" ] (read restricted)
        let! omitted = Service.reminders service (completed "") token
        expectRows database [ 102, "COMPLETED"; 104, "COMPLETED"; 105, "COMPLETED" ] (read omitted)

        let! contradictory =
            Service.reminders
                service
                (wire """{"view":"COMPLETED","state":"PENDING","includeInactive":true,"limit":50}""")
                token

        expectRows database [] (read contradictory)

        use host =
            new TestServerFixture.Host(
                true,
                installationId = database.Fixture.InstallationId,
                forActor = (fun principal -> core database.Fixture principal)
            )

        let! _ = host.Login()
        let! credential = host.Token()

        let export query expected =
            task {
                use! response =
                    host.CallResponse(
                        ExportTransport.token,
                        Codec.encode ExportTransport.request (ExportSelection.Reminders query),
                        credential
                    )

                Expect.equal
                    (int response.StatusCode)
                    200
                    "Real server materializes the selected CSV"

                Expect.equal
                    (nonNull response.Content.Headers.ContentType).MediaType
                    "text/csv"
                    "CSV success writer"

                let! bytes = response.Content.ReadAsByteArrayAsync()

                Expect.equal
                    response.Content.Headers.ContentLength
                    (Nullable(int64 bytes.Length))
                    "Exact complete CSV length"

                let actual =
                    csvRows bytes
                    |> List.map (fun row -> Guid.Parse row["reminderId"], row["state"])

                Expect.equal
                    actual
                    (expected |> List.map (fun (number, state) -> id database.Fixture number, state))
                    "CSV contains exact literal selected identities and states"
            }

        do!
            export
                (wire
                    """{"target":{"kind":"REFERENCE","caseReference":"STATE-OPEN"},"state":"ANY","limit":50}""")
                [ 101, "PENDING"; 102, "COMPLETED"; 103, "CANCELLED" ]

        do! export (completed ",\"includeInactive\":false") [ 102, "COMPLETED" ]
        do! export (completed "") [ 102, "COMPLETED"; 104, "COMPLETED"; 105, "COMPLETED" ]
    }

[<Tests>]
let tests =
    testList
        "reminder selection result boundaries"
        [
            testCaseAsync
                "case Core link returns all three actual states while omitted no-view selection stays pending"
                (run coreLink)
            testCaseAsync
                "Wire ANY traverses literal pages counts and scoped rights without broadening"
                (run pagingAndRights)
            testCaseAsync
                "explicit false excludes closed and voided completed rows in queries and real CSV"
                (run inactiveAndExport)
        ]
