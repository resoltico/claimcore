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
            testCase "typed row destinations travel with their captions and cursor" (fun () ->
                let values = documents ()

                let links =
                    values
                    |> List.map (fun value ->
                        value.Screen.Regions
                        |> List.collect (fun region ->
                            match region.Content with
                            | RegionContent.Table table -> table.Rows |> List.choose _.Link
                            | _ -> []))

                Expect.equal links[0] [ Link.caseById parent ] "Case identity"
                Expect.equal links[2] [ Link.person actor.ActorId ] "Person identity"

                Expect.equal
                    (values |> List.map _.NextCursor)
                    [ Some "case-next"; Some "reminder-next"; Some "actor-next" ]
                    "Cursors"

                for value in values do
                    Expect.isOk
                        (Codec.encode HumanDocumentCodecs.document value
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Native boundary")
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
