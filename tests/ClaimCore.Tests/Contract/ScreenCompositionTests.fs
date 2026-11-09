module ClaimCore.Tests.ScreenCompositionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Cli
open ClaimCore.TestSupport

let private data (endpoint: Endpoint<'request, Outcome<Presented<'reply>>>) =
    match ResponseExamples.defaultReply endpoint with
    | Outcome.Succeeded reply -> reply.Data
    | _ -> failtest "The independent response example needs a successful read"

let private roles (screen: Screen) = screen.Regions |> List.map _.Role

let private matches name expected build =
    testCase name (fun () ->
        let screen = build ()
        Expect.equal (roles screen) expected "Roles and order define the screen contract"
        Expect.isTrue (Screen.valid screen.Regions) "Only facts repeat")

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
                    TimelineChange.Payments
                        {
                            Entries = []
                            Before = None
                            After =
                                Some
                                    {
                                        Paid = CaseFixtures.money "100" "EUR"
                                        Payable = CaseFixtures.money "300" "EUR"
                                        Remaining = CaseFixtures.money "200" "EUR"
                                    }
                        }

                let disclosure =
                    TimelineDisclosure.create
                        [ change ]
                        {
                            Event = TimelineEvent.CaseActRecorded CaseActKind.RecordPayment
                            Highlights = [ TimelineHighlight.Change 0 ]
                        }
                    |> Result.defaultWith invalidOp

                let receipt = ResponseExamples.receipt

                let entry =
                    {
                        OperationId = receipt.OperationId
                        Position = receipt.Position
                        RecordedAt = receipt.RecordedAt
                        RecordedBy = receipt.RecordedBy
                        Subject =
                            TimelineSubject.CaseFact(Revision.first, CaseActKind.RecordPayment)
                        Changes = TimelineChanges.Disclosed disclosure
                    }

                let detail = TimelineScreen.timelineDetail entry |> Option.get

                Expect.equal
                    (detail.Highlights |> List.map _.TitleId)
                    [ "label.paymentTotals" ]
                    "The progress sentence needs arguments and cannot be a section heading")
            testCase
                "timeline metadata survives in details without disclosing withheld business changes"
                (fun () ->
                    let receipt = ResponseExamples.receipt

                    let item =
                        TimelineScreen.item
                            {
                                OperationId = receipt.OperationId
                                Position = receipt.Position
                                RecordedAt = receipt.RecordedAt
                                RecordedBy = receipt.RecordedBy
                                Subject =
                                    TimelineSubject.CaseFact(Revision.first, CaseActKind.Open)
                                Changes = TimelineChanges.Withheld
                            }

                    let detailValues = item.Details |> List.collect _.Entries |> List.map _.Value

                    Expect.contains
                        detailValues
                        (PresentedValue.Identifier(OperationId.format receipt.OperationId))
                        "The operation identity is available under Record details"

                    Expect.contains
                        detailValues
                        (PresentedValue.Identifier "1")
                        "The recorded version is retained"

                    Expect.isEmpty item.Highlights "Withheld changes never become highlights")
            matches
                "case workspace"
                [
                    RegionRole.Identity
                    RegionRole.Attention
                    RegionRole.Progress
                    RegionRole.Recommendation
                    RegionRole.Facts
                    RegionRole.Payments
                    RegionRole.Work
                    RegionRole.NextActions
                    RegionRole.Notes
                    RegionRole.Details
                ]
                (fun () -> CaseScreen.view ResponseExamples.caseRead)
            matches "case list" [ RegionRole.Results; RegionRole.Details ] (fun () ->
                data Api.caseList |> CaseListScreen.view)
            matches "case totals" [ RegionRole.Totals ] (fun () ->
                data Api.caseSummary |> CaseTotalsScreen.view)
            matches
                "reminder"
                [ RegionRole.Identity; RegionRole.Facts; RegionRole.Details ]
                (fun () -> data Api.reminder |> ReminderScreen.view)
            matches "reminder list" [ RegionRole.Results; RegionRole.Details ] (fun () ->
                data Api.reminders |> ReminderListScreen.view)
            matches "timeline" [ RegionRole.Activity; RegionRole.Details ] (fun () ->
                data Api.caseTimeline |> TimelineScreen.view)
            matches "people" [ RegionRole.Results; RegionRole.Details ] (fun () ->
                data Api.authorityList |> PeopleScreen.view)
            matches
                "person"
                [ RegionRole.Account; RegionRole.Access; RegionRole.Details ]
                (fun () -> data Api.authorityGrants |> PersonScreen.view)
            matches "access history" [ RegionRole.Activity; RegionRole.Details ] (fun () ->
                data Api.authorityHistory |> AccessHistoryScreen.view)
            matches "access requests" [ RegionRole.Results; RegionRole.Details ] (fun () ->
                data Api.authorityRequests |> AccessRequestsScreen.view)
            matches
                "context"
                [ RegionRole.Identity; RegionRole.Facts; RegionRole.Details ]
                (fun () -> data Api.context |> ContextScreen.view)
            matches "operation pending" [ RegionRole.Notice ] (fun () ->
                OperationScreen.pending Inputs.workBasis.ObservedAt)
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
                            [ "case"; "get"; "CASE-1"; "--details" ]
                        |> Result.defaultWith (fun error -> failtestf "%A" error)

                    Expect.isTrue prepared.Details "The flag reaches the renderer")
        ]
