module ClaimCore.IntegrationTests.TimelineOverviewReadTests

open System
open System.Text
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.Postgres

let private required =
    function
    | Ok value -> value
    | Error problem -> failtestf "Fixture refused: %A" problem

let private id (ids: OperationIds.Scope) number = ids.Id number

let private token = CancellationToken.None
let private get (value: System.Threading.Tasks.Task<'value>) = value.GetAwaiter().GetResult()

let private actor ids number name =
    {
        Actor.ActorId = CaseFixtures.actorId (id ids number)
        Principal = PrincipalKey.human "https://timeline-reads.example/" name |> required
        Enabled = true
        Profile = ActorProfile.parse { DisplayName = name; Contact = None } |> required |> Some
    }

let private calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"
        member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
    }

let private service (database: PostgresFixture.Database) (person: Actor) =
    Service.create
        "timeline-overview-tests"
        database.Fixture.InstallationId
        database.Fixture.Store
        calendar
        person.Principal

let private execute core input =
    match (Service.execute core input token |> get).Outcome with
    | ExecuteResult.Accepted receipt -> receipt
    | other -> failtestf "Expected accepted fixture: %A" other

let private case ids number revision act =
    Inputs.caseOperation
        {
            OperationId = id ids number
            CaseReference = "TIMELINE-OVERVIEW"
            ExpectedRevision = revision
            Act = act
        }

let private authority ids number (person: Actor) act =
    let kind, issuer, subject = PrincipalKey.parts person.Principal

    OperationInput.Authority
        {
            OperationId = id ids number
            Target =
                {
                    Kind = kind
                    Issuer = issuer
                    Subject = subject
                }
            Act = act
        }

let private page core filter cursor limit : CaseTimelinePage =
    match
        (Service.timeline
            core
            {
                Target = CaseSelector.Reference "TIMELINE-OVERVIEW"
                Filter = filter
                AfterCursor = cursor
                Limit = limit
            }
            token
         |> get)
    with
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected readable timeline: %A" other

let private collect core filter =
    let rec next cursor collected =
        let current = page core filter cursor 1
        let all = collected @ current.Items

        match current.NextCursor with
        | Some cursor -> next (Some cursor) all
        | None -> all

    next None []

let private disclosed (entry: CaseTimelineEntry) =
    match entry.Changes with
    | TimelineChanges.Disclosed value -> value
    | other -> failtestf "Expected full disclosure: %A" other

let private fixture () =
    let ids = OperationIds.Scope()

    let owner, assigned, reader, steward =
        actor ids 2 "Timeline owner",
        actor ids 3 "Original assignee",
        actor ids 4 "Summary reader",
        actor ids 5 "Steward"

    let database = PostgresFixture.createFor owner.Principal |> get

    let database =
        { database with
            Fixture =
                { database.Fixture with
                    OperationIds = ids
                }
        }

    try
        let core = service database owner

        let provisioned =
            Service.provisionOwner
                core
                (id ids 2)
                {
                    DisplayName = "Timeline owner"
                    Contact = None
                }
                token
            |> get

        match provisioned with
        | Ok(Verdict.Proceed _) -> ()
        | other -> failtestf "Provisioning failed: %A" other

        for person, number, name in
            [
                assigned, 3, "Original assignee"
                reader, 4, "Summary reader"
                steward, 5, "Steward"
            ] do
            execute
                core
                (authority
                    ids
                    number
                    person
                    (AuthorityAct.Register { DisplayName = name; Contact = None }))
            |> ignore

        for number, person, role in
            [
                1000, owner, Role.CaseHandler
                1001, owner, Role.PaymentRecorder
                1002, assigned, Role.CaseHandler
                1003, steward, Role.DataSteward
            ] do
            execute
                core
                (authority
                    ids
                    number
                    person
                    (AuthorityAct.Grant
                        {
                            Role = role
                            Scope = GrantTarget.Installation
                        }))
            |> ignore

        let receipt =
            execute core (case ids 10 0L (AuthoredCaseAct.Open Inputs.registration))

        let parent =
            match receipt.Subject with
            | ReceiptSubject.Case value -> value.CaseId
            | _ -> failtest "Expected case receipt"

        execute
            core
            (authority
                ids
                1004
                reader
                (AuthorityAct.Grant
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseId parent
                    }))
        |> ignore

        execute core (case ids 11 1L (AuthoredCaseAct.Decide Inputs.decision)) |> ignore

        for number, action, due in
            [
                12, "First authored action", "2026-11-08"
                13, "Second authored action", "2026-11-09"
            ] do
            execute
                core
                (OperationInput.Reminder
                    {
                        OperationId = id ids number
                        Target = CaseSelector.Reference "TIMELINE-OVERVIEW"
                        Act = AuthoredReminderAct.Create { Action = action; Due = due }
                    })
            |> ignore

        execute
            core
            (OperationInput.Work
                {
                    OperationId = id ids 14
                    Target = CaseSelector.Reference "TIMELINE-OVERVIEW"
                    ExpectedRevision = 0L
                    Act =
                        WorkAct.UpdateWork
                            {
                                Responsible = WorkValue.Set(ActorId.format assigned.ActorId)
                                Summary = WorkValue.Set "Exact authored handling summary"
                            }
                })
        |> ignore

        database, core, owner, assigned, reader, steward
    with error ->
        FixtureDisposal.afterFailure error [ database.Fixture.Dispose ]
        reraise ()

let private ledgerCount (database: PostgresFixture.Database) =
    DatabaseSession.read database.OwnerSource token (fun connection ->
        task {
            let! rows =
                DatabaseSession.rows connection "SELECT count(*) FROM claimcore.ledger" [||] token

            return DatabaseSession.value<int64> 0 (List.exactlyOne rows)
        })
    |> get

let private reminder ids number act =
    OperationInput.Reminder
        {
            OperationId = id ids number
            Target = CaseSelector.Reference "TIMELINE-OVERVIEW"
            Act = act
        }

let private stalePreview ids core number identity expected =
    let input =
        reminder
            ids
            number
            (AuthoredReminderAct.Update(
                ReminderId.value identity,
                expected,
                {
                    Action = "Stale proposed action"
                    Due = "2026-11-25"
                }
            ))

    match (Service.preview core input token |> get).Outcome with
    | Outcome.Rejected(Refusal.RevisionConflict conflict) ->
        match conflict.Changes with
        | Some(ConflictChanges.Page page) -> conflict.ActualRevision, page
        | other -> failtestf "Expected disclosed conflict page: %A" other
    | other -> failtestf "Expected stale preview refusal: %A" other

let private conflictContinuation core (changes: ChangePage) =
    let cursor =
        changes.NextCursor
        |> Option.defaultWith (fun () ->
            failtest "A fifty-entry conflict must issue its continuation")

    let page = page core None (Some cursor) 50

    Expect.isNone
        page.NextCursor
        "Only the scoped paired birth remains after its fifty newer changes"

    Expect.equal
        page.Items.Length
        1
        "The continuation remains confined to one entity/revision range"

    List.head page.Items

[<Tests>]
let tests =
    testList
        "Core timeline overview reads"
        [
            testCase
                "zero reminder revisions refuse cleanly without append while revision one stays valid"
                (fun () ->
                    let database, core, _, _, _, _ = fixture ()
                    let ids = database.Fixture.OperationIds

                    use _owned =
                        { new IDisposable with
                            member _.Dispose() = database.Fixture.Dispose()
                        }

                    let before = ledgerCount database

                    let target =
                        CaseFixtures.operationId (id ids 12)
                        |> ReminderId.ofOperationId
                        |> ReminderId.value

                    let malformed =
                        reminder
                            ids
                            401
                            (AuthoredReminderAct.Update(
                                target,
                                0L,
                                {
                                    Action = "Invalid zero"
                                    Due = "2026-11-15"
                                }
                            ))

                    let check =
                        function
                        | Refusal.InvalidInput issues ->
                            let values = InputIssues.toList issues
                            Expect.equal values.Length 1 "Zero has one targeted issue"

                            Expect.equal
                                values.Head.Target
                                InputTarget.ExpectedReminderRevision
                                "Existing targeted reminder diagnostic is retained"

                            Expect.equal
                                values.Head.Violation
                                Violation.ExpectedRevisionRange
                                "The range refusal is not a storage exception"
                        | other -> failtestf "Expected targeted input refusal: %A" other

                    match (Service.preview core malformed token |> get).Outcome with
                    | Outcome.Rejected refusal -> check refusal
                    | other -> failtestf "Expected clean preview refusal: %A" other

                    match (Service.execute core malformed token |> get).Outcome with
                    | ExecuteResult.Rejected refusal -> check refusal
                    | other -> failtestf "Expected clean execute refusal: %A" other

                    Expect.equal
                        (ledgerCount database)
                        before
                        "The authorized invalid-input snapshot appends no ledger entry"

                    Expect.equal
                        (collect core None).Length
                        5
                        "No existing case/reminder event changed"

                    let valid =
                        reminder
                            ids
                            402
                            (AuthoredReminderAct.Update(
                                target,
                                1L,
                                {
                                    Action = "Valid one"
                                    Due = "2026-11-15"
                                }
                            ))

                    match (Service.preview core valid token |> get).Outcome with
                    | Outcome.Succeeded(Previewed.Reviewed _) -> ()
                    | other -> failtestf "Revision one remains reviewable: %A" other

                    execute core valid |> ignore)
            testCase
                "accepted paired effects stay isolated in both stale previews and fixed-range continuations"
                (fun () ->
                    let database, core, owner, _, _, _ = fixture ()
                    let ids = database.Fixture.OperationIds

                    use _owned =
                        { new IDisposable with
                            member _.Dispose() = database.Fixture.Dispose()
                        }

                    let original = ReminderId.ofOperationId (CaseFixtures.operationId (id ids 12))

                    let paired =
                        execute
                            core
                            (reminder
                                ids
                                40
                                (AuthoredReminderAct.CompleteAndPlanNext(
                                    ReminderId.value original,
                                    1L,
                                    {
                                        Action = "Private paired next action"
                                        Due = "2026-11-10"
                                    }
                                )))

                    let completed, next =
                        match paired.Subject with
                        | ReceiptSubject.Reminder {
                                                      Effects = ReminderReceiptEffects.CompletedAndPlanned(completed,
                                                                                                           next)
                                                  } -> completed, next
                        | other -> failtestf "Both effects must be atomically accepted: %A" other

                    Expect.equal
                        completed.ReminderId
                        original
                        "The receipt identifies the actual completed reminder"

                    Expect.equal
                        (Revision.value completed.Revision)
                        2L
                        "Original effect advances exactly once"

                    Expect.equal
                        (Revision.value next.Revision)
                        1L
                        "Next effect is born at revision one"

                    execute
                        core
                        (reminder
                            ids
                            41
                            (AuthoredReminderAct.Reopen(
                                ReminderId.value original,
                                2L,
                                "2026-11-11"
                            )))
                    |> ignore

                    for revision in 1L .. 49L do
                        execute
                            core
                            (reminder
                                ids
                                (100 + int revision)
                                (AuthoredReminderAct.Update(
                                    ReminderId.value original,
                                    revision + 2L,
                                    {
                                        Action = sprintf "Original update %d" revision
                                        Due = "2026-11-11"
                                    }
                                )))
                        |> ignore

                    for revision in 1L .. 51L do
                        execute
                            core
                            (reminder
                                ids
                                (200 + int revision)
                                (AuthoredReminderAct.Update(
                                    ReminderId.value next.ReminderId,
                                    revision,
                                    {
                                        Action = sprintf "Next update %d" revision
                                        Due = "2026-11-12"
                                    }
                                )))
                        |> ignore

                    let originalActual, originalPage = stalePreview ids core 301 original 1L
                    let nextActual, nextPage = stalePreview ids core 302 next.ReminderId 1L

                    Expect.equal
                        (Revision.value originalActual)
                        52L
                        "Original conflict observes the actual fixed upper revision"

                    Expect.equal
                        (Revision.value nextActual)
                        52L
                        "Next conflict fixes the legitimate positive-revision range"

                    Expect.equal
                        originalPage.Entries.Length
                        50
                        "Existing conflict admission budget remains fifty"

                    Expect.equal nextPage.Entries.Length 50 "Next range obeys the same budget"

                    execute
                        core
                        (reminder
                            ids
                            303
                            (AuthoredReminderAct.Update(
                                ReminderId.value original,
                                52L,
                                {
                                    Action = "Future original action"
                                    Due = "2026-11-20"
                                }
                            )))
                    |> ignore

                    execute
                        core
                        (reminder
                            ids
                            304
                            (AuthoredReminderAct.Update(
                                ReminderId.value next.ReminderId,
                                52L,
                                {
                                    Action = "Future next action"
                                    Due = "2026-11-21"
                                }
                            )))
                    |> ignore

                    let oldEntry = conflictContinuation core originalPage
                    let nextEntry = conflictContinuation core nextPage

                    Expect.equal
                        oldEntry.OperationId
                        paired.OperationId
                        "Original continuation retains the accepted paired operation"

                    Expect.isGreaterThan
                        nextEntry.Position.Sequence
                        oldEntry.Position.Sequence
                        "A legitimate next-reminder conflict starts after birth"

                    Expect.equal
                        nextEntry.OperationId
                        (CaseFixtures.operationId (id ids 201))
                        "The next conflict retains its actual first update operation"

                    Expect.equal
                        (disclosed oldEntry).Overview.Event
                        (TimelineEvent.ReminderCompleted(
                            ReminderAction.parse "First authored action" |> required
                        ))
                        "Original continuation uses its retained action, not latest state"

                    Expect.equal
                        (disclosed nextEntry).Overview.Event
                        (TimelineEvent.ReminderChanged(
                            ReminderAction.parse "Next update 1" |> required
                        ))
                        "Next continuation uses its retained first update, not latest state"

                    // Existing reminder ids commands require positive revisions; only this Core-issued history range includes birth revision one.
                    let currentPage = page core None None 1
                    let scope = TimelineHistory.binding currentPage.CaseId owner.ActorId None

                    let birthRange =
                        {
                            Entity = ChangeEntity.Reminder next.ReminderId
                            ExpectedRevision = Revision.zero
                            ActualRevision = Revision.first
                        }

                    let before =
                        CaseFixtures.sequence (Sequence.value paired.Position.Sequence + 1L)

                    let birthCursor =
                        ClaimCore.RecordFormat.Cursors.encodeTimeline
                            database.Fixture.InstallationId
                            scope
                            (Some birthRange)
                            before

                    let birthPage = page core None (Some birthCursor) 50

                    Expect.equal
                        birthPage.Items.Length
                        1
                        "Core-issued next-birth range selects the accepted paired operation"

                    let nextBirth = List.head birthPage.Items

                    Expect.equal
                        nextBirth.OperationId
                        paired.OperationId
                        "The next birth is part of the same accepted operation"

                    Expect.equal
                        nextBirth.Position
                        oldEntry.Position
                        "Both scoped birth effects keep their true shared ledger position"

                    Expect.equal
                        (disclosed nextBirth).Overview.Event
                        (TimelineEvent.ReminderPlanned(
                            ReminderAction.parse "Private paired next action" |> required,
                            CaseFixtures.date "2026-11-10"
                        ))
                        "Birth context is scoped before overview construction"

                    let birthWire =
                        Codec.encode TimelineCodecs.timelineEntry nextBirth
                        |> Encoding.UTF8.GetString

                    for forbidden in
                        [
                            "First authored action"
                            "2026-11-08"
                            "Future original action"
                            "Future next action"
                        ] do
                        Expect.isFalse
                            (birthWire.Contains(forbidden, StringComparison.Ordinal))
                            "Next paired effect excludes original action/date and later updates"

                    let oldWire =
                        Codec.encode TimelineCodecs.timelineEntry oldEntry
                        |> Encoding.UTF8.GetString

                    let nextWire =
                        Codec.encode TimelineCodecs.timelineEntry nextEntry
                        |> Encoding.UTF8.GetString

                    for forbidden in
                        [
                            "Private paired next action"
                            "2026-11-10"
                            "Future original action"
                            "Future next action"
                        ] do
                        Expect.isFalse
                            (oldWire.Contains(forbidden, StringComparison.Ordinal))
                            "Original scoped event/full changes exclude the other effect and later writes"

                    for forbidden in
                        [
                            "First authored action"
                            "2026-11-08"
                            "Future original action"
                            "Future next action"
                        ] do
                        Expect.isFalse
                            (nextWire.Contains(forbidden, StringComparison.Ordinal))
                            "Next scoped event/full changes exclude original context and later writes")
            testCase
                "shared snapshot producer pages all five events and honors current names and history rights"
                (fun () ->
                    let database, core, _, assigned, reader, _ = fixture ()
                    let ids = database.Fixture.OperationIds

                    use _owned =
                        { new IDisposable with
                            member _.Dispose() = database.Fixture.Dispose()
                        }

                    let initial = collect core None
                    Expect.equal initial.Length 5 "Page boundaries retain every ledger event"

                    Expect.equal
                        ((disclosed (List.item 0 initial)).Overview.Event)
                        (TimelineEvent.ResponsibilityAssigned(Actor.currentReference assigned))
                        "Responsible name is supplied by the Core snapshot"

                    Expect.equal
                        ((disclosed (List.item 0 initial)).Overview.Highlights)
                        [ TimelineHighlight.Change 1 ]
                        "Only summary difference is highlighted"

                    match
                        (disclosed (List.item 1 initial)).Overview.Event,
                        (disclosed (List.item 2 initial)).Overview.Event
                    with
                    | TimelineEvent.ReminderPlanned(next, due),
                      TimelineEvent.ReminderPlanned(first, _) ->
                        Expect.equal
                            (ReminderAction.format next)
                            "Second authored action"
                            "Newest reminder retains its exact authored action"

                        Expect.equal
                            (CalendarDate.format due)
                            "2026-11-09"
                            "Due date is typed and exact"

                        Expect.equal
                            (ReminderAction.format first)
                            "First authored action"
                            "The older page uses its own retained effect"
                    | other -> failtestf "Unexpected event order: %A" other

                    Expect.equal
                        ((disclosed (List.item 3 initial)).Overview.Event)
                        (TimelineEvent.CaseActRecorded CaseActKind.Decide)
                        "Decision event is not reconstructed by the web"

                    Expect.equal
                        ((disclosed (List.item 4 initial)).Overview.Highlights)
                        []
                        "OPEN remains one semantic event outside details"

                    Expect.isGreaterThan
                        (disclosed (List.item 4 initial)).Items.Length
                        1
                        "Full initial fields remain present"

                    Expect.equal
                        (collect core (Some TimelineFilter.Facts) |> List.length)
                        2
                        "Core filter and cursor stay independent of overview shape"

                    Expect.equal
                        (collect core (Some TimelineFilter.Work) |> List.length)
                        3
                        "Both reminder and work entries remain ordered"

                    execute
                        core
                        (authority
                            ids
                            20
                            assigned
                            (AuthorityAct.SetProfile
                                {
                                    DisplayName = "Current renamed assignee"
                                    Contact = None
                                }))
                    |> ignore

                    match (collect core None |> List.head |> disclosed).Overview.Event with
                    | TimelineEvent.ResponsibilityAssigned value ->
                        Expect.equal
                            value.DisplayName
                            (DisplayName.Named(CaseFixtures.name "Current renamed assignee"))
                            "Names refresh independently of accepted case-work values"
                    | other -> failtestf "%A" other

                    let summaryOnly = collect (service database reader) None

                    Expect.equal
                        summaryOnly.Length
                        5
                        "Summary permission still sees allowed metadata"

                    Expect.isTrue
                        (summaryOnly
                         |> List.forall (fun value -> value.Changes = TimelineChanges.Withheld))
                        "No overview can carry full values across the disclosure union"

                    let reply =
                        { (page core None None 50) with
                            Items = summaryOnly
                        }

                    let bytes =
                        Codec.encode
                            Api.caseTimeline.Output
                            (Outcome.Succeeded(
                                Presented.create
                                    (fun (page: CaseTimelinePage) ->
                                        Descriptor.tableView Descriptor.timelineColumns page.Items)
                                    reply
                            ))
                        |> Encoding.UTF8.GetString

                    Expect.isFalse
                        (bytes.Contains("Current renamed assignee", StringComparison.Ordinal))
                        "Assignment context is not a summary-permission escape"

                    execute core (authority ids 21 assigned AuthorityAct.EraseProfile) |> ignore

                    match (collect core None |> List.head |> disclosed).Overview.Event with
                    | TimelineEvent.ResponsibilityAssigned value ->
                        Expect.equal
                            value.DisplayName
                            DisplayName.Erased
                            "A removed profile never falls back to accepted or IdP names"
                    | other -> failtestf "%A" other)
            testCase
                "erased parent exposes no overview values and unavailable callers retain non-disclosure"
                (fun () ->
                    let database, core, _, _, _, steward = fixture ()
                    let ids = database.Fixture.OperationIds

                    use _owned =
                        { new IDisposable with
                            member _.Dispose() = database.Fixture.Dispose()
                        }

                    let stewardCore = service database steward

                    execute stewardCore (case ids 30 2L (AuthoredCaseAct.Void "Retained reason"))
                    |> ignore

                    execute stewardCore (case ids 31 3L (AuthoredCaseAct.Bare BareAct.Erase))
                    |> ignore

                    let erased = collect core None

                    Expect.isTrue
                        (erased
                         |> List.forall (fun value -> value.Changes = TimelineChanges.Erased))
                        "No projection runs over erased effects"

                    let unknown = actor ids 90 "Unknown caller"

                    match
                        (Service.timeline
                            (service database unknown)
                            {
                                Target = CaseSelector.Reference "TIMELINE-OVERVIEW"
                                Filter = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token
                         |> get)

                    with
                    | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
                    | other -> failtestf "Existing non-disclosure must remain: %A" other)
        ]
