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

let private documents () =
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
                            cases.CaseIds
                            [ parent ]
                            "Case destination is an ID, not a displayed reference"

                        let reminderControl = List.exactlyOne reminders.Reminders
                        Expect.equal reminderControl.CaseId parent "Reminder route retains parent identity"
                        Expect.equal reminderControl.ReminderId (Reminder.id reminder) "Reminder identity is typed"

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

                                if kind = "CASES" then context["caseIds"] <- JsonArray()
                                elif kind = "PEOPLE" then context["actorIds"] <- JsonArray()
                                else context["reminders"] <- JsonArray())

                        let duplicate =
                            mutate (fun node ->
                                let context = node["context"] |> nonNull
                                let kind = (context["kind"] |> nonNull).GetValue<string>()

                                let key =
                                    if kind = "CASES" then "caseIds"
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
        ]
