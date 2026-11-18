module ClaimCore.IntegrationTests.TimelineOverviewReadTests

open System
open System.Text
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.Application

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
        Profile =
            ActorProfile.parse
                {
                    DisplayName = name
                    Contact = None
                    DigestEmail = None
                }
            |> required
            |> Some
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
    match (Service.executeSubmission core input token |> get).Outcome with
    | ExecuteResult.Accepted receipt -> receipt
    | other -> failtestf "Expected accepted fixture: %A" other

let private case (core: ServiceContext) ids number revision taskName values =
    let declaration =
        ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral taskName)
        |> Option.get

    let reference = "TIMELINE-OVERVIEW"

    let binding, values =
        if declaration.Creates then
            OperationBinding.CaseNew revision,
            values
            |> Map.add
                (Submissions.field BusinessField.CaseReference)
                (SubmissionValue.Text reference)
        else
            let family =
                if declaration.ReviewsFamilySequence then
                    Some(
                        StoreFixtures.reviewedFamilySequence
                            core.Store
                            (CaseSelector.Reference reference)
                    )
                else
                    None

            OperationBinding.Case(CaseSelector.Reference reference, revision, None, family), values

    Submissions.createRaw taskName (id ids number) binding values

let private people (core: ServiceContext) ids number (person: Actor) taskName values =
    StoreFixtures.personSubmission core.Store (id ids number) person.Principal None taskName values
    |> get

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
                    DigestEmail = None
                    Contact = None
                }
                []
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
                (people
                    (core)
                    (ids)
                    (number)
                    (person)
                    "people.register"
                    (ClaimCore.TestSupport.Submissions.profile (
                        {
                            DisplayName = name
                            Contact = None
                            DigestEmail = None
                        }
                    )))
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
                (people
                    (core)
                    (ids)
                    (number)
                    (person)
                    "people.grant"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = role
                            Scope = GrantTarget.Installation
                        }
                    )))
            |> ignore

        let receipt =
            execute
                core
                (case
                    (core)
                    (ids)
                    (10)
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (DomainInputs.registration)
                    )))

        let parent =
            match receipt.Subject with
            | ReceiptSubject.Case value -> value.CaseId
            | _ -> failtest "Expected case receipt"

        execute
            core
            (people
                (core)
                (ids)
                (1004)
                (reader)
                "people.grant"
                (ClaimCore.TestSupport.Submissions.grant (
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseId parent
                    }
                )))
        |> ignore

        execute
            core
            (case
                (core)
                (ids)
                (11)
                (1L)
                "case.decide"
                (ClaimCore.TestSupport.Submissions.fields (
                    DecisionInput.values (DomainInputs.decision)
                )))
        |> ignore

        for number, action, due in
            [
                12, "First authored action", "2026-11-08"
                13, "Second authored action", "2026-11-09"
            ] do
            execute
                core
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "reminder.create"
                    (id ids number)
                    (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                        (CaseSelector.Reference "TIMELINE-OVERVIEW"),
                        None
                    ))
                    (ClaimCore.TestSupport.Submissions.reminderPlan (action) (due)))
            |> ignore

        execute
            core
            ((ClaimCore.TestSupport.Submissions.createRaw)
                "work.update"
                (id ids 14)
                (ClaimCore.Application.Contracts.OperationBinding.Work(
                    (CaseSelector.Reference "TIMELINE-OVERVIEW"),
                    (0L)
                ))
                (ClaimCore.TestSupport.Submissions.work (
                    {
                        Responsible = WorkValue.Set(ActorId.format assigned.ActorId)
                        Summary = WorkValue.Set "Exact authored handling summary"
                    }
                )))
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

let private reminder ids number taskName selected values =
    Submissions.createRaw
        taskName
        (id ids number)
        (OperationBinding.Reminder((CaseSelector.Reference "TIMELINE-OVERVIEW"), selected))
        values

let private stalePreview ids core number identity expected =
    let input =
        reminder
            (ids)
            (number)
            "reminder.update"
            (Some((ReminderId.value identity), (expected)))
            (ClaimCore.TestSupport.Submissions.reminderPlan ("Stale proposed action") ("2026-11-25"))

    match (Service.previewSubmission core input token |> get).Outcome with
    | Outcome.Rejected(Refusal.RevisionConflict conflict) ->
        match conflict.Changes with
        | Some(ConflictChanges.Page page) -> conflict.ActualRevision, page
        | other -> failtestf "Expected disclosed conflict page: %A" other
    | other -> failtestf "Expected stale preview refusal: %A" other

let private conflictContinuation core (changes: ChangePage) =
    let cursor =
        changes.NextCursor
        |> Option.defaultWith (fun () -> failtest "A bounded conflict must issue its continuation")

    let rec remaining cursor accumulated =
        let current = page core None (Some cursor) 50
        Expect.isLessThanOrEqual current.Items.Length 10 "Full-detail pages remain bounded."
        let items = accumulated @ current.Items

        match current.NextCursor with
        | Some next -> remaining next items
        | None -> items

    let items = remaining cursor []
    Expect.equal items.Length 41 "Every remaining entry is confined to the observed entity range."
    List.last items

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
                            (ids)
                            (401)
                            "reminder.update"
                            (Some((target), (0L)))
                            (ClaimCore.TestSupport.Submissions.reminderPlan
                                ("Invalid zero")
                                ("2026-11-15"))

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

                    match (Service.previewSubmission core malformed token |> get).Outcome with
                    | Outcome.Rejected refusal -> check refusal
                    | other -> failtestf "Expected clean preview refusal: %A" other

                    match (Service.executeSubmission core malformed token |> get).Outcome with
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
                            (ids)
                            (402)
                            "reminder.update"
                            (Some((target), (1L)))
                            (ClaimCore.TestSupport.Submissions.reminderPlan
                                ("Valid one")
                                ("2026-11-15"))

                    match (Service.previewSubmission core valid token |> get).Outcome with
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
                                (ids)
                                (40)
                                "reminder.completeAndPlanNext"
                                (Some((ReminderId.value original), (1L)))
                                (ClaimCore.TestSupport.Submissions.reminderPlan
                                    ("Private paired next action")
                                    ("2026-11-10")))

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
                            (ids)
                            (41)
                            "reminder.reopen"
                            (Some((ReminderId.value original), (2L)))
                            (Map.ofList
                                [
                                    TaskInputId.Target InputTarget.ReminderDueDate,
                                    ClaimCore.Application.Contracts.SubmissionValue.Text(
                                        "2026-11-11"
                                    )
                                ]))
                    |> ignore

                    for revision in 1L .. 49L do
                        execute
                            core
                            (reminder
                                (ids)
                                ((100 + int revision))
                                "reminder.update"
                                (Some((ReminderId.value original), (revision + 2L)))
                                (ClaimCore.TestSupport.Submissions.reminderPlan
                                    (sprintf "Original update %d" revision)
                                    ("2026-11-11")))
                        |> ignore

                    for revision in 1L .. 51L do
                        execute
                            core
                            (reminder
                                (ids)
                                ((200 + int revision))
                                "reminder.update"
                                (Some((ReminderId.value next.ReminderId), (revision)))
                                (ClaimCore.TestSupport.Submissions.reminderPlan
                                    (sprintf "Next update %d" revision)
                                    ("2026-11-12")))
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
                        10
                        "Full-detail conflict pages preserve the response bound"

                    Expect.equal nextPage.Entries.Length 10 "Next range obeys the same budget"

                    execute
                        core
                        (reminder
                            (ids)
                            (303)
                            "reminder.update"
                            (Some((ReminderId.value original), (52L)))
                            (ClaimCore.TestSupport.Submissions.reminderPlan
                                ("Future original action")
                                ("2026-11-20")))
                    |> ignore

                    execute
                        core
                        (reminder
                            (ids)
                            (304)
                            "reminder.update"
                            (Some((ReminderId.value next.ReminderId), (52L)))
                            (ClaimCore.TestSupport.Submissions.reminderPlan
                                ("Future next action")
                                ("2026-11-21")))
                    |> ignore

                    let oldEntry = conflictContinuation core originalPage
                    let nextEntry = conflictContinuation core nextPage

                    Expect.equal
                        oldEntry.OperationId
                        paired.OperationId
                        "Original continuation retains the accepted paired operation"

                    Expect.isGreaterThan
                        nextEntry.Position
                        oldEntry.Position
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
                        EffectPosition.create
                            (CaseFixtures.sequence (Sequence.value paired.Position.Sequence + 1L))
                            0
                        |> Option.get

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
                        (EffectPosition.sequence nextBirth.Position)
                        (EffectPosition.sequence oldEntry.Position)
                        "Both scoped effects share the accepting root sequence"

                    Expect.equal
                        nextBirth.Position
                        (EffectPosition.create (EffectPosition.sequence oldEntry.Position) 1
                         |> Option.get)
                        "The next reminder is born at the second effect"

                    Expect.equal
                        oldEntry.Position
                        (EffectPosition.create (EffectPosition.sequence nextBirth.Position) 0
                         |> Option.get)
                        "The original reminder is completed at the first effect"

                    Expect.equal
                        (disclosed nextBirth).Overview.Event
                        (TimelineEvent.ReminderPlanned(
                            ReminderAction.parse "Private paired next action" |> required,
                            CaseFixtures.date "2026-11-10"
                        ))
                        "Birth context is scoped before overview construction"

                    let birthWire =
                        TimelineCodecs.timelineEntry.Write nextBirth
                        |> JsonValue.tryMember "changes"
                        |> Option.get
                        |> CanonicalWriter.bytes
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
                        TimelineCodecs.timelineEntry.Write oldEntry
                        |> JsonValue.tryMember "changes"
                        |> Option.get
                        |> CanonicalWriter.bytes
                        |> Encoding.UTF8.GetString

                    let nextWire =
                        TimelineCodecs.timelineEntry.Write nextEntry
                        |> JsonValue.tryMember "changes"
                        |> Option.get
                        |> CanonicalWriter.bytes
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
                        (TimelineEvent.PartChanged ClaimCore.Domain.Changes.PartKind.Case)
                        "Decision event is not reconstructed by the web"

                    let opening = disclosed (List.item 4 initial)

                    Expect.equal
                        opening.Overview.Event
                        (TimelineEvent.PartChanged ClaimCore.Domain.Changes.PartKind.Case)
                        "Core supplies the opening part event"

                    Expect.isTrue
                        (opening.Overview.Highlights
                         |> List.forall (function
                             | TimelineHighlight.Change index ->
                                 index >= 0 && index < opening.Items.Length
                             | _ -> true))
                        "Every core-selected opening highlight addresses its disclosed details"

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
                        (people
                            (core)
                            (ids)
                            (20)
                            (assigned)
                            "people.setProfile"
                            (ClaimCore.TestSupport.Submissions.profile (
                                {
                                    DisplayName = "Current renamed assignee"
                                    DigestEmail = None
                                    Contact = None
                                }
                            )))
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
                            (RepresentationReply.Data(Outcome.Succeeded reply))
                        |> Encoding.UTF8.GetString

                    Expect.isFalse
                        (bytes.Contains("Current renamed assignee", StringComparison.Ordinal))
                        "Assignment context is not a summary-permission escape"

                    execute
                        core
                        (people (core) (ids) (21) (assigned) "people.eraseProfile" (Map.empty))
                    |> ignore

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

                    execute
                        stewardCore
                        (case
                            (core)
                            (ids)
                            (30)
                            (2L)
                            "case.void"
                            (Map.ofList
                                [
                                    TaskInputId.Target InputTarget.Reason,
                                    ClaimCore.Application.Contracts.SubmissionValue.Text(
                                        "Retained reason"
                                    )
                                ]))
                    |> ignore

                    execute stewardCore (case (core) (ids) (31) (3L) "case.erase" (Map.empty))
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
