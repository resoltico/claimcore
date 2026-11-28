module ClaimCore.Tests.ScreenCompositionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Cli
open ClaimCore.TestSupport

let private data
    (endpoint: Endpoint<'request, RepresentationReply<HumanReadReply, Outcome<'reply>>>)
    =
    match ResponseExamples.defaultReply endpoint with
    | RepresentationReply.Data(Outcome.Succeeded reply) -> reply
    | _ -> failtest "The independent response example needs a successful read"

let private roles (screen: Screen) = screen.Regions |> List.map _.Role

let private matches name expected build =
    testCase name (fun () ->
        let screen = build ()
        Expect.equal (roles screen) expected "Roles and order define the screen contract"
        Expect.isTrue (Screen.valid screen.Regions) "Only facts repeat")

[<Tests>]
let redesignDeclarations =
    testList
        "workspace presentation declarations"
        [
            testCase "opening exposes only server-assigned permanent references" (fun () ->
                let reference = TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)

                Expect.isFalse
                    (ClaimCore.Domain.Tasks.Case.Open.declaration.Inputs
                     |> List.exists (fun input -> input.Input = reference))
                    "The opening declaration has no authored reference")
            testCase "all chip vocabularies declare exactly one supported tone per code" (fun () ->
                let tokens vocabulary =
                    Vocabulary.name vocabulary, Set.ofList (Vocabulary.tokens vocabulary)

                let expected =
                    [
                        tokens CaseHeadline.vocabulary
                        tokens CaseStatus.vocabulary
                        tokens Lifecycle.vocabulary
                        tokens CaseProgress.vocabulary
                        tokens CaseAttention.vocabulary
                        tokens ReminderDue.vocabulary
                        tokens ReminderState.vocabulary
                        tokens DeadlineOutcome.vocabulary
                        tokens AccountState.vocabulary
                        tokens RequesterKind.vocabulary
                        tokens ClosureOutcome.vocabulary
                    ]
                    |> Map.ofList

                Expect.equal
                    (Tones.codeTones |> Map.map (fun _ tones -> Set.ofSeq (Map.keys tones)))
                    expected
                    "The tone table covers the admitted vocabularies"

                Expect.equal
                    (Tones.codeTones["attention"]["DEADLINE_OVERDUE"])
                    "DANGER"
                    "An overdue deadline uses the danger tone"

                Expect.equal
                    (Tones.codeTones["headline"]["CLOSED"])
                    "NEUTRAL"
                    "Closed is not an alert")
            testCase "work view prominence matches the case and reminder tab sets" (fun () ->
                Expect.equal
                    (CaseView.all
                     |> List.filter (WorkViewsScreen.caseProminence >> (=) Prominence.Primary)
                     |> Set.ofList)
                    (set
                        [
                            CaseView.NeedsAttention
                            CaseView.Mine
                            CaseView.Overdue
                            CaseView.DueToday
                            CaseView.All
                        ])
                    "Case tabs and More views"

                Expect.equal
                    (ReminderView.all
                     |> List.filter (WorkViewsScreen.reminderProminence >> (=) Prominence.Primary)
                     |> Set.ofList)
                    (set
                        [
                            ReminderView.Pending
                            ReminderView.Mine
                            ReminderView.Overdue
                            ReminderView.Today
                            ReminderView.Upcoming
                        ])
                    "Reminder tabs and More views")
            testCase "HUMAN worklist columns carry the declared order and prominence" (fun () ->
                let region = (CaseListScreen.rows []).Regions.Head
                let primary = Prominence.Primary
                let secondary = Prominence.Secondary
                let detail = Prominence.Detail

                match region.Content with
                | RegionContent.Table table ->
                    Expect.equal
                        (table.Columns |> List.map (fun column -> column.Key, column.Prominence))
                        [
                            "caseReference", primary
                            "headline", primary
                            "claimants", primary
                            "claimedAmount", secondary
                            "responsible", secondary
                            "nextAction", secondary
                            "nextDue", secondary
                            "attention", primary
                            "insurers", detail
                            "payableAmount", detail
                            "latestTransferDate", detail
                            "changedAt", detail
                        ]
                        "Human columns are independent of the machine projection"

                    Expect.equal
                        (table.Columns
                         |> List.choose (fun column ->
                             column.Joins |> Option.map (fun target -> column.Key, target)))
                        [ "nextDue", "nextAction"; "attention", "headline" ]
                        "Human Cases declares its two joins"

                    Expect.equal
                        region.EmptyId
                        (Some "label.noCasesForView")
                        "Cases supplies its empty message"
                | _ -> failtest "Cases results are a table"

                for view in ReminderView.all do
                    let baseColumns =
                        [ "due"; "action"; "reference"; "claimants"; "kind"; "responsible" ]

                    let expected =
                        match view with
                        | ReminderView.Completed
                        | ReminderView.Cancelled -> baseColumns @ [ "doneOn"; "deadlineOutcome" ]
                        | _ -> baseColumns

                    Expect.equal
                        (ReminderListScreen.columnsFor (Some view) None |> List.map _.Key)
                        expected
                        "Finished reminder views add the two completion columns")
            testCase
                "closed headline includes the reason and case fact presence is explicit"
                (fun () ->
                    let reading = ResponseExamples.caseRead

                    let closed =
                        { reading with
                            Headline = CaseHeadline.Closed
                            Snapshot =
                                { reading.Snapshot with
                                    State =
                                        reading.Snapshot.State
                                        |> CaseState.map (fun facts ->
                                            { facts with
                                                Handling =
                                                    Handling.Closed
                                                        {
                                                            Outcome = ClosureOutcome.Settled
                                                            Note = None
                                                        }
                                            })
                                }
                        }

                    let regions = (CaseScreen.view closed).Regions

                    let facts role =
                        regions
                        |> List.filter (fun region -> region.Role = role)
                        |> List.collect (fun region ->
                            match region.Content with
                            | RegionContent.Entries entries -> entries
                            | _ -> [])

                    Expect.contains
                        (facts RegionRole.Identity |> List.map _.Value)
                        (PresentedValue.State(
                            CaseHeadline.Closed,
                            Some("closureOutcome", "SETTLED")
                        ))
                        "The closed headline contains the typed outcome"

                    Expect.isTrue
                        (regions
                         |> List.forall (fun region ->
                             match region.Content with
                             | RegionContent.Entries entries ->
                                 List.forall (fun (entry: Entry) -> entry.Recorded.IsSome) entries
                             | _ -> true))
                        "Every entry region declares presence"

                    Expect.isTrue
                        (facts RegionRole.Facts
                         |> List.exists (fun entry -> entry.Recorded = Some false))
                        "An absent optional fact is explicit"

                    match
                        (regions |> List.find (fun region -> region.Role = RegionRole.NextActions))
                            .Content
                    with
                    | RegionContent.Table table ->
                        Expect.isEmpty table.Sections "Next actions excludes the old summary"
                    | _ -> failtest "Next actions require a table"

                    let nextOnly =
                        regions
                        |> List.filter (fun region -> region.Role = RegionRole.NextActions)
                        |> Screen.create

                    Expect.equal
                        (ViewText.renderSelected false (RenderingContext.forLanguage "en") nextOnly)
                        [ "No next actions for this case." ]
                        "CLI renders the empty message in place of the column headers")
            testCase "an empty case party table names its own empty state" (fun () ->
                let original = ResponseExamples.caseRead

                let add =
                    TaskOffer.Available(
                        TaskId.ofLiteral "party.add",
                        Binding.PartyNew(CaseSelector.Id original.Snapshot.CaseId),
                        {
                            Seeds = Map.empty
                            Selections = Set.empty
                            Editable = []
                            Choices = Map.empty
                            Applicable = true
                            Blocked = None
                            Recommendation = None
                        }
                    )

                let observation =
                    { original with
                        Parties = []
                        PartyOffers = [ add ]
                    }

                let screen = CaseScreen.view observation

                let parties =
                    screen.Regions |> List.find (fun region -> region.Role = RegionRole.Parties)

                match parties.Content with
                | RegionContent.Table table ->
                    Expect.isEmpty table.Rows "No party rows are synthesized"

                    Expect.equal
                        parties.EmptyId
                        (Some "empty.parties")
                        "The party region owns its empty message"
                | _ -> failtest "The party region requires a table")
            testCase
                "payment-decision affordance uses a label for the current decision state"
                (fun () ->
                    let reading = ResponseExamples.caseRead

                    let binding =
                        Binding.Case(
                            CaseSelector.Id reading.Snapshot.CaseId,
                            reading.Snapshot.Revision
                        )

                    let offer = Inputs.offeredTask "case.decide" binding Map.empty

                    let actionWith state =
                        HumanCaseDocument.ofCore
                            { reading with
                                Snapshot = { reading.Snapshot with State = state }
                                Advice = CaseAdvice.withOffers [ offer ] reading.Advice
                            }
                        |> _.Screen
                        |> Screen.affordances
                        |> List.find (fun action -> TaskId.token action.Task = "case.decide")

                    let changed =
                        reading.Snapshot.State
                        |> CaseState.map (fun facts ->
                            { facts with
                                Finance =
                                    FinancialPosition.withDecision
                                        (Some(DomainInputs.admittedDecision DomainInputs.decision))
                                        facts.Finance
                            })

                    Expect.equal
                        (actionWith reading.Snapshot.State).LabelId
                        "task.case.decide.label"
                        "First decision"

                    Expect.equal
                        (actionWith changed).LabelId
                        "task.case.decide.change"
                        "Existing decision")
            testCase
                "current bindings omit inapplicable case tasks and retain actionable blocks"
                (fun () ->
                    let declaration = ClaimCore.Domain.Tasks.Case.Reopen.declaration

                    let binding =
                        Binding.Case(
                            CaseSelector.Id ResponseExamples.snapshot.CaseId,
                            Revision.zero
                        )

                    let offer refusal =
                        ClaimCore.Domain.Tasks.Declarations.availability declaration (Some refusal)
                        |> TaskOffer.forBinding declaration.Id binding

                    match offer (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided)) with
                    | TaskOffer.Declared task ->
                        Expect.equal task declaration.Id "The state makes the task inapplicable"
                    | _ -> failtest "Reopen on an open case must not become an affordance"

                    match offer Refusal.RegisterMaintenance with
                    | TaskOffer.Available(_, _, availability) ->
                        Expect.equal
                            availability.Blocked
                            (Some Refusal.RegisterMaintenance)
                            "Keep advice for an actionable block"
                    | _ -> failtest "Maintenance keeps the task observable")
            testCase "task declaration rejects implicit stewardship review" (fun () ->
                Expect.throwsT<System.ArgumentException>
                    (fun () ->
                        ClaimCore.Domain.Tasks.Declarations.familyWithReview
                            "case.close"
                            ActSubject.Case
                            false
                            ActFamily.Business
                            Consequence.Stewardship
                            TaskReview.Implicit
                            []
                            []
                        |> ignore)
                    "Review constraints are enforced at declaration construction")
            testCase
                "only routine note addition and reminder completion allow implicit review"
                (fun () ->
                    let tasks =
                        TaskDeclarations.all
                        |> List.filter (fun task -> task.Review = Some TaskReview.Implicit)

                    Expect.equal
                        (tasks |> List.map _.Id |> Set.ofList)
                        (set [ "note.add"; "reminder.complete" ])
                        "The implicit review set is closed"

                    Expect.isTrue
                        (tasks |> List.forall (fun task -> task.Consequence = Consequence.Routine))
                        "An implicit task must be routine"

                    for collection in [ TaskCollectionId.Parties; TaskCollectionId.PeopleGrants ] do
                        let labels =
                            [
                                CollectionPresentation.labelId collection
                                CollectionPresentation.itemLabelId collection
                                CollectionPresentation.addLabelId collection
                                CollectionPresentation.removeLabelId collection
                            ]

                        Expect.equal
                            (Set.ofList labels).Count
                            4
                            "Each collection has four distinct wording roles")
        ]

[<Tests>]
let invalidTables =
    testCase "screen constructors refuse duplicate rows widths and incomplete row groups" (fun () ->
        let row key values =
            {
                Link = None
                Actions = []
                RegionRow.Key = key
                Choice = None
                Transfers = []
                Recommended = None
                Prominence = None
                Values = values
                Detail = None
            }

        let column: Column =
            {
                Recorded = false
                Key = "value"
                LabelId = "label.details"
                Prominence = Prominence.Primary
                IsMoney = false
                Joins = None
            }

        let values = [ PresentedValue.Text "retained" ]

        let table rows groups =
            {
                Columns = [ column ]
                Rows = rows
                Groups = groups
                Sections = []
                Meter = None
            }

        let invalid =
            [
                table [ row "same" values; row "same" values ] []
                table [ row "one" [] ] []
                table
                    [ row "one" values ]
                    [
                        {
                            TitleId = "label.details"
                            Count = 1L
                            RowKeys = [ "other" ]
                        }
                    ]
            ]

        let valid =
            Screen.create
                [
                    {
                        Link = None
                        Actions = []
                        Role = RegionRole.Results
                        TitleId = None
                        EmptyId = None
                        Count = None
                        Content = RegionContent.Table(table [ row "one" values ] [])
                    }
                ]

        Expect.equal valid.Regions.Length 1 "A valid table still constructs"

        for value in invalid do
            Expect.throwsT<System.ArgumentException>
                (fun () ->
                    Screen.create
                        [
                            {
                                Link = None
                                Actions = []
                                Role = RegionRole.Results
                                TitleId = None
                                EmptyId = None
                                Count = None
                                Content = RegionContent.Table value
                            }
                        ]
                    |> ignore)
                "Invalid table structure is refused before serialization")

[<Tests>]
let tests =
    testList
        "screen composition"
        [
            testCase "single reminder details retain the server date basis" (fun () ->
                let view = data Api.reminder |> ReminderScreen.view

                let details =
                    view.Regions |> List.find (fun region -> region.Role = RegionRole.Details)

                match details.Content with
                | RegionContent.Entries entries ->
                    let basis =
                        entries |> List.map (fun entry -> entry.Key, entry.Value) |> Map.ofList

                    Expect.equal
                        basis["timeZone"]
                        (PresentedValue.Text "UTC")
                        "The single record retains the installation zone"

                    Expect.isTrue
                        (basis.ContainsKey "businessDate" && basis.ContainsKey "observedAt")
                        "Date and observation travel with the zone"
                | _ -> failtest "Reminder details contain entries")
            testCase "payment activity uses a heading without amount placeholders" (fun () ->
                let change =
                    TimelineChange.Balances
                        [
                            {
                                Currency =
                                    Currency.parse "EUR" |> Result.defaultWith (string >> failtest)
                                Net =
                                    {
                                        Before = None
                                        After =
                                            Some(
                                                DecimalTotal.parse "100"
                                                |> Result.defaultWith (string >> failtest)
                                            )
                                    }
                            }
                        ]

                let disclosure =
                    TimelineDisclosure.create
                        [ change ]
                        {
                            Event = TimelineEvent.PartChanged PartKind.Payment
                            Highlights = [ TimelineHighlight.Change 0 ]
                        }
                    |> Result.defaultWith invalidOp

                let receipt = ResponseExamples.receipt

                let entry =
                    {
                        OperationId = receipt.OperationId
                        Position = EffectPosition.create receipt.Position.Sequence 0 |> Option.get
                        OperationHash = receipt.Position.EntryHash
                        RecordedAt = receipt.RecordedAt
                        RecordedBy = receipt.RecordedBy |> AuthorityRef.actor |> Option.get
                        Subject =
                            {
                                PartKind = PartKind.Payment
                                PartId = OperationId.value receipt.OperationId
                                Revision = Revision.first
                            }
                        Task = TaskId.ofLiteral "case.recordPayment"
                        Changes = TimelineChanges.Disclosed disclosure
                    }

                let detail = TimelineScreen.timelineDetail entry |> Option.get

                Expect.equal
                    (detail.Highlights |> List.map _.TitleId)
                    [ "label.netPaid" ]
                    "The progress sentence needs arguments and cannot be a section heading")
            testCase
                "timeline metadata survives in details without disclosing withheld business changes"
                (fun () ->
                    let receipt = ResponseExamples.receipt

                    let item =
                        TimelineScreen.item
                            {
                                OperationId = receipt.OperationId
                                Position =
                                    EffectPosition.create receipt.Position.Sequence 0 |> Option.get
                                OperationHash = receipt.Position.EntryHash
                                RecordedAt = receipt.RecordedAt
                                RecordedBy = receipt.RecordedBy |> AuthorityRef.actor |> Option.get
                                Subject =
                                    {
                                        PartKind = PartKind.Case
                                        PartId = CaseId.value ResponseExamples.snapshot.CaseId
                                        Revision = Revision.first
                                    }
                                Task = TaskId.ofLiteral "case.open"
                                Changes = TimelineChanges.Withheld
                            }

                    let detailValues = item.Details |> List.collect _.Entries |> List.map _.Value

                    Expect.contains
                        detailValues
                        (PresentedValue.Identifier(OperationId.format receipt.OperationId))
                        "The operation identity is available under Technical details"

                    Expect.contains
                        detailValues
                        (PresentedValue.Identifier "1")
                        "The recorded version is retained"

                    Expect.isEmpty item.Changes "Withheld comparisons carry no business values"

                    Expect.equal
                        (item.Highlights |> List.collect _.Entries |> List.map _.Value)
                        [ PresentedValue.Absent "label.changesWithheld" ]
                        "The compact row discloses only the withheld state")
            testCase "current case identity holds the subject and excludes status facts" (fun () ->
                let current =
                    { ResponseExamples.caseRead with
                        Attention = [ CaseAttention.DeadlineOverdue ]
                    }

                let screen = CaseScreen.view current
                let document = HumanCaseDocument.ofCore current

                let identity (screen: Screen) =
                    screen.Regions |> List.find (fun region -> region.Role = RegionRole.Identity)

                Expect.equal
                    (identity document.Screen)
                    (identity screen)
                    "The current-case document used by editors preserves the case screen identity"

                let entries role =
                    screen.Regions
                    |> List.collect (fun region ->
                        match region.Content with
                        | RegionContent.Entries entries when region.Role = role -> entries
                        | _ -> [])

                Expect.equal
                    (entries RegionRole.Identity |> List.map _.Key)
                    [ "caseReference"; "headline"; "attention"; "claimedAmount" ]
                    "Only subject facts appear in the identity"

                Expect.contains
                    (entries RegionRole.Identity |> List.map _.Value)
                    (PresentedValue.Codes("attention", [ "DEADLINE_OVERDUE" ]))
                    "Attention accompanies the headline"

                let facts = entries RegionRole.Facts |> List.map _.Key
                Expect.contains facts "status" "Case status belongs to facts"
                Expect.contains facts "progress" "Payment progress belongs to facts")
            matches
                "case workspace"
                [
                    RegionRole.Identity
                    RegionRole.Progress
                    RegionRole.Facts
                    RegionRole.Payments
                    RegionRole.Work
                    RegionRole.NextActions
                    RegionRole.Notes
                    RegionRole.Details
                ]
                (fun () -> CaseScreen.view ResponseExamples.caseRead)
            matches "case list" [ RegionRole.Results ] (fun () ->
                data Api.caseList |> CaseListScreen.view)
            matches "paged payments" [ RegionRole.Identity; RegionRole.Payments ] (fun () ->
                data Api.casePayments |> PaymentsScreen.page)
            testCase "erased payment page labels lifecycle separately from case status" (fun () ->
                let page =
                    { data Api.casePayments with
                        State = CaseState.Erased
                    }

                let output =
                    PaymentsScreen.page page
                    |> ViewText.render (RenderingContext.forLanguage "en")
                    |> String.concat "\n"

                Expect.stringContains
                    output
                    "Lifecycle: Erased"
                    "Erasure is a lifecycle, not an operator status"

                Expect.isFalse (output.Contains "Case status:") "No case status survives erasure")
            matches "paged notes" [ RegionRole.Identity; RegionRole.Notes ] (fun () ->
                data Api.caseNotes |> NotesScreen.page)
            matches "assignee choices" [ RegionRole.Results; RegionRole.Details ] (fun () ->
                data Api.caseAssignees |> AssigneesScreen.view)
            testCase
                "undecided payment page supplies financial count and absent totals without a meter"
                (fun () ->
                    let screen = data Api.casePayments |> PaymentsScreen.page

                    match screen.Regions[1].Content with
                    | RegionContent.Table table ->
                        Expect.isNone table.Meter "Undecided position has no progress meter"
                        let entries = table.Sections |> List.collect _.Entries

                        Expect.equal
                            (entries |> List.map _.Key)
                            [ "paymentCount"; "paymentDecisionDate"; "payable"; "decisionBasis" ]
                            "Authored summary order"

                        Expect.equal
                            entries.Head.Value
                            (PresentedValue.Count 0L)
                            "Known zero count"

                        Expect.equal
                            screen.Regions[1].Count
                            (Some
                                {
                                    CountKind = "payments"
                                    Quantity = 0L
                                })
                            "Payment count is declared on the region"

                        let decision =
                            table.Sections
                            |> List.find (fun section -> section.TitleId = "label.paymentDecision")

                        Expect.equal
                            decision.EmptyMessageId
                            (Some "empty.paymentDecision")
                            "The undecided view declares its empty message"

                        Expect.isTrue
                            (decision.Entries
                             |> List.forall (fun entry -> entry.Recorded = Some false))
                            "Every unrecorded decision value is explicitly absent"
                    | _ -> failtest "Financial summary table required")
            matches
                "case totals"
                [ RegionRole.Notice; RegionRole.Totals; RegionRole.Results ]
                (fun () -> data Api.caseSummary |> CaseTotalsScreen.view)
            matches
                "reminder"
                [ RegionRole.Identity; RegionRole.Facts; RegionRole.Details ]
                (fun () -> data Api.reminder |> ReminderScreen.view)
            matches "reminder list" [ RegionRole.Results ] (fun () ->
                data Api.reminders |> ReminderListScreen.view None None)
            matches "timeline" [ RegionRole.Activity ] (fun () ->
                data Api.caseTimeline |> TimelineScreen.view)
            matches "people" [ RegionRole.Results ] (fun () ->
                data Api.peopleList |> PeopleScreen.view)
            matches
                "person"
                [ RegionRole.Account; RegionRole.Access; RegionRole.Details ]
                (fun () -> data Api.peopleGrants |> PersonScreen.view)
            matches "access history" [ RegionRole.Activity ] (fun () ->
                data Api.peopleHistory |> AccessHistoryScreen.view)
            testCase
                "empty access requests omit local scaffolding without consuming pagination metadata"
                (fun () ->
                    let reply = data Api.peopleRequests
                    let empty = { reply with Items = [] }

                    let regions = (AccessRequestsScreen.view empty).Regions

                    Expect.equal
                        (regions |> List.map _.Role)
                        [ RegionRole.Results ]
                        "The empty state has no pagination details"

                    Expect.equal
                        regions.Head.EmptyId
                        (Some "label.noAccessRequests")
                        "The region provides its empty message"

                    Expect.equal
                        empty.Basis
                        reply.Basis
                        "Presentation does not rewrite the read basis"

                    Expect.equal
                        empty.NextCursor
                        reply.NextCursor
                        "The HUMAN page retains its continuation")
            matches "empty access requests" [ RegionRole.Results ] (fun () ->
                data Api.peopleRequests |> AccessRequestsScreen.view)
            matches
                "context"
                [ RegionRole.Identity; RegionRole.Facts; RegionRole.Details ]
                (fun () -> data Api.context |> ContextScreen.view)
            matches "operation pending" [ RegionRole.Notice ] (fun () ->
                OperationScreen.pending DomainInputs.workBasis.ObservedAt)
            matches "operation not accepted" [ RegionRole.Notice ] OperationScreen.notAccepted
            testCase "receipt separates done facts and technical details" (fun () ->
                let screen = ReceiptScreen.view ResponseExamples.receipt

                Expect.equal
                    screen.Regions.Head.Role
                    RegionRole.Notice
                    "One done notice starts the receipt"

                Expect.equal
                    (List.last screen.Regions).Role
                    RegionRole.Details
                    "Technical metadata is last"

                match (List.last screen.Regions).Content with
                | RegionContent.Entries entries ->
                    let operation = entries |> List.find (fun entry -> entry.Key = "operationId")

                    Expect.equal
                        operation.Recorded
                        (Some true)
                        "The receipt's known operation identity remains present"
                | _ -> failtest "Receipt details contain the operation identity"

                Expect.isTrue (Screen.valid screen.Regions) "Role uniqueness holds"

                for region in screen.Regions do
                    match region.Content with
                    | RegionContent.Entries entries when region.Role <> RegionRole.Details ->
                        Expect.isFalse
                            (entries |> List.exists ScreenEntries.technical)
                            "IDs and versions only live in DETAILS"
                    | _ -> ())
            testCase
                "CLI details flag is explicit and requires no operation identity for a read"
                (fun () ->
                    let prepared =
                        Grammar.prepare
                            (fun () -> failtest "A read must not allocate an operation ID")
                            [ "case"; "get"; "CC-55KW-SSJ4-8HMS-G1BH-73"; "--details" ]
                        |> Result.defaultWith (fun error -> failtestf "%A" error)

                    Expect.isTrue prepared.Details "The flag reaches the renderer")
        ]
