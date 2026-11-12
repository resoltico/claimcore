module ClaimCore.Tests.NavigationDocumentTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private parent =
    CaseFixtures.caseId (Guid.Parse "00000000-0000-4000-8000-000000000001")

let private actor =
    Inputs.namedActorReference
        (CaseFixtures.actorId (Guid.Parse "00000000-0000-4000-8000-000000000002"))
        "Core account caption"

let private reminder =
    Reminder.ofAccepted
        (ReminderId.create (Guid.Parse "00000000-0000-4000-8000-000000000003")
         |> Option.get)
        parent
        Revision.first
        (ReminderAction.parse "Core action caption"
         |> Result.defaultWith (string >> failtest))
        (CalendarDate.ofDate Inputs.workBasis.BusinessDate)
        ReminderState.Pending
    |> Result.defaultWith failtest

let case: CaseSummary =
    {
        CaseId = parent
        Reference = Inputs.reference "CASE-NAV"
        Revision = Revision.first
        WorkRevision = Revision.zero
        CanAssign = false
        ChangedAt = Inputs.workBasis.ObservedAt
        State = CaseState.Erased
        Headline = CaseHeadline.Erased
        Attention = []
        Stages = None
        NextAction = None
        Responsible = None
        PendingReminders = 0L
        Paid = None
        PaymentCount = 0L
        LastPaymentDate = None
    }

let private documents () =
    let person: ActorReviewItem =
        {
            Actor = actor
            Principal =
                PrincipalKey.human "https://issuer.example/" "navigation"
                |> Result.defaultWith (string >> failtest)
            Contact = None
            State = AccountState.NoRoles
            Roles = []
            ResponsibleCases = None
        }

    let action: ReminderReadModel =
        {
            Reminder = reminder
            Reference = case.Reference
            ClaimantName = Inputs.name "Core claimant"
            Responsible = None
            RecordedAt = Inputs.workBasis.ObservedAt
            RecordedBy = actor
            Acts = []
            Active = true
            Timing = Some ReminderDue.Today
        }

    [
        HumanReadDocuments.cases
            {
                Basis = Inputs.workBasis
                Items = [ case ]
                Offers = []
                NextCursor = Some "case-next"
                ExactReference = None
            }
        HumanReadDocuments.reminders
            {
                Basis = Inputs.workBasis
                Items = [ action ]
                NextCursor = Some "reminder-next"
            }
        HumanReadDocuments.actors
            {
                Basis = Inputs.workBasis
                Items = [ person ]
                NextCursor = Some "actor-next"
            }
    ]

[<Tests>]
let tests =
    testList
        "human list navigation"
        [
            testCase
                "authorized list producers supply bounded typed destinations independent of core captions"
                (fun () ->
                    let values = documents ()

                    match values[0].Context, values[1].Context, values[2].Context with
                    | InteractionContext.Cases cases,
                      InteractionContext.Reminders reminders,
                      InteractionContext.People people ->
                        Expect.equal
                            (cases.Cases |> List.map _.CaseId)
                            [ parent ]
                            "Case destination is an ID, not a displayed reference"

                        let reminderControl = List.exactlyOne reminders.Reminders

                        Expect.equal
                            reminderControl.CaseId
                            parent
                            "Reminder route retains parent identity"

                        Expect.equal
                            reminderControl.ReminderId
                            (Reminder.id reminder)
                            "Reminder identity is typed"

                        Expect.equal
                            people.ActorIds
                            [ actor.ActorId ]
                            "Person destination is independent of label"
                    | _ -> failtest "Dedicated list controls required"

                    for document in values do
                        Expect.isSome
                            (HumanDocument.nextCursor document)
                            "Native list traversal recognizes typed page controls"

                        let caption (region: Region) =
                            match region.Content with
                            | RegionContent.Table table when region.Role = RegionRole.Results ->
                                let rows =
                                    table.Rows
                                    |> List.map (fun row ->
                                        { row with
                                            Values =
                                                table.Columns
                                                |> List.map (fun _ ->
                                                    PresentedValue.Text "opaque-list-caption Ω")
                                        })

                                { region with
                                    Content = RegionContent.Table { table with Rows = rows }
                                }
                            | _ -> region

                        let opaque =
                            { document with
                                Screen =
                                    { document.Screen with
                                        Regions = document.Screen.Regions |> List.map caption
                                    }
                            }

                        let bytes = Codec.encode HumanDocumentCodecs.document opaque

                        Expect.isOk
                            (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                            "Control bindings do not depend on parsed display values"

                        Expect.equal
                            opaque.Context
                            document.Context
                            "Caption substitution does not change navigation")
            testCase
                "list controls reject mismatched or duplicated rows and tightened disclosure"
                (fun () ->
                    for document in documents () do
                        let bytes = Codec.encode HumanDocumentCodecs.document document

                        let mutate edit =
                            let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                            edit node
                            Encoding.UTF8.GetBytes(node.ToJsonString())

                        let mismatch =
                            mutate (fun node ->
                                let context = node["context"] |> nonNull
                                let kind = (context["kind"] |> nonNull).GetValue<string>()

                                if kind = "CASES" then context["cases"] <- JsonArray()
                                elif kind = "PEOPLE" then context["actorIds"] <- JsonArray()
                                else context["reminders"] <- JsonArray())

                        let duplicate =
                            mutate (fun node ->
                                let context = node["context"] |> nonNull
                                let kind = (context["kind"] |> nonNull).GetValue<string>()

                                let key =
                                    if kind = "CASES" then "cases"
                                    elif kind = "PEOPLE" then "actorIds"
                                    else "reminders"

                                let items = (context[key] |> nonNull).AsArray()
                                items.Add((items[0] |> nonNull).DeepClone()))

                        let hidden =
                            mutate (fun node ->
                                node["disclosure"] <-
                                    System.Text.Json.Nodes.JsonValue.Create "WITHHELD")

                        for raw in [ mismatch; duplicate; hidden ] do
                            Expect.isError
                                (Codec.validate 65536 HumanDocumentCodecs.document raw)
                                "A valid row requires its exact permitted handle")
            testCase
                "case list supplies only authorized assignment seeds and exact destinations"
                (fun () ->
                    let row =
                        { case with
                            State = CaseState.Active { Fields = Map.empty }
                            CanAssign = true
                            Responsible = Some actor
                            WorkRevision = Revision.first
                        }

                    let page item =
                        HumanReadDocuments.cases
                            {
                                Basis = Inputs.workBasis
                                Items = [ item ]
                                Offers = []
                                NextCursor = None
                                ExactReference =
                                    Some
                                        {
                                            CaseId = parent
                                            CaseReference = case.Reference
                                        }
                            }

                    let value = page row

                    match value.Context with
                    | InteractionContext.Cases control ->
                        Expect.equal
                            control.ExactCaseId
                            (Some parent)
                            "Authorized exact destination"

                        let binding =
                            (List.exactlyOne control.Cases).Assignment
                            |> Option.defaultWith (fun () ->
                                failtest "Assignment binding required")

                        Expect.equal binding.Revision Revision.first "Selected list work revision"

                        Expect.equal
                            binding.ResponsibleSeed
                            (Some actor.ActorId)
                            "Permitted editable assignee seed"
                    | _ -> failtest "Case list controls required"

                    match (page { row with CanAssign = false }).Context with
                    | InteractionContext.Cases control ->
                        Expect.isNone
                            (List.exactlyOne control.Cases).Assignment
                            "No editable seed without assignment offer"
                    | _ -> failtest "Case list controls required"

                    Expect.isOk
                        (Codec.encode HumanDocumentCodecs.document value
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Actual native boundary")
            testCase
                "work view controls bind typed selections to supplied label and count rows"
                (fun () ->
                    let value =
                        HumanReadDocuments.views
                            {
                                Basis = Inputs.workBasis
                                CaseViews = [ { View = CaseView.All; Count = 17L } ]
                                ReminderViews =
                                    [
                                        {
                                            View = ReminderView.Pending
                                            Count = 23L
                                        }
                                    ]
                            }

                    match value.Context with
                    | InteractionContext.WorkViews control ->
                        Expect.equal
                            (List.exactlyOne control.Cases).Key
                            "CASE:ALL"
                            "Case row identity"

                        Expect.equal
                            (List.exactlyOne control.Cases).View
                            CaseView.All
                            "Typed case selection"

                        Expect.equal
                            (List.exactlyOne control.Reminders).Key
                            "REMINDER:PENDING"
                            "Reminder row identity"

                        Expect.equal
                            (List.exactlyOne control.Reminders).View
                            ReminderView.Pending
                            "Typed reminder selection"
                    | _ -> failtest "Work view controls required"

                    let bytes = Codec.encode HumanDocumentCodecs.document value

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "Actual native work views"

                    let invalid = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                    (nonNull invalid["context"])["cases"] <- JsonArray()

                    Expect.isError
                        (Encoding.UTF8.GetBytes(invalid.ToJsonString())
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Rows cannot outlive their typed selections")
            testCase
                "Core navigation totals use disjoint urgent buckets and never invent a missing count"
                (fun () ->
                    let value =
                        {
                            Basis = Inputs.workBasis
                            CaseViews =
                                [
                                    {
                                        View = CaseView.NeedsAttention
                                        Count = 5L
                                    }
                                ]
                            ReminderViews =
                                [
                                    {
                                        View = ReminderView.Overdue
                                        Count = 1L
                                    }
                                    {
                                        View = ReminderView.Today
                                        Count = 2L
                                    }
                                ]
                        }

                    Expect.equal
                        (WorkViewCounts.casesNeedingAttention value)
                        (Some 5L)
                        "Core case attention count"

                    Expect.equal
                        (WorkViewCounts.remindersNeedingAttention value)
                        (Some 3L)
                        "Disjoint urgent views"

                    Expect.isNone
                        (WorkViewCounts.remindersNeedingAttention
                            { value with
                                ReminderViews =
                                    [
                                        {
                                            View = ReminderView.Overdue
                                            Count = 1L
                                        }
                                    ]
                            })
                        "Missing snapshot input is not zero"

                    let document = HumanReadDocuments.views value

                    let totals =
                        document.Screen.Regions
                        |> List.find (fun region -> region.Role = RegionRole.Totals)

                    match totals.Content with
                    | RegionContent.Entries entries ->
                        Expect.equal
                            (entries |> List.map (fun entry -> entry.Key, entry.Value))
                            [
                                "cases", PresentedValue.Count 5L
                                "reminders", PresentedValue.Count 3L
                            ]
                            "Supplied navigation facts"
                    | _ -> failtest "Navigation totals required")
        ]
