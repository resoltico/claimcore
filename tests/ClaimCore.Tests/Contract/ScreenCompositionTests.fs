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
            matches
                "case workspace"
                [
                    RegionRole.Identity
                    RegionRole.Attention
                    RegionRole.Progress
                    RegionRole.Recommendation
                    RegionRole.Facts
                    RegionRole.Facts
                    RegionRole.Payments
                    RegionRole.Work
                    RegionRole.NextActions
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
                (fun () -> (data Api.reminder).Item |> ReminderScreen.view)
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
