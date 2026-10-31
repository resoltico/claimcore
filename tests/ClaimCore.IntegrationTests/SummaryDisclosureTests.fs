module ClaimCore.IntegrationTests.SummaryDisclosureTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private summaries () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            let fixture = database.Fixture
            let! owner = caseWorker fixture |> Async.AwaitTask

            for number, reference in [ 100, "VISIBLE"; 101, "PRIVATE" ] do
                let! result =
                    Service.execute
                        owner
                        (OperationInput.Case(
                            request number reference 0L (AuthoredCaseAct.Open Inputs.registration)
                        ))
                        token
                    |> Async.AwaitTask

                accepted result |> ignore

            let! decided =
                Service.execute
                    owner
                    (OperationInput.Case(
                        request 102 "VISIBLE" 1L (AuthoredCaseAct.Decide Inputs.decision)
                    ))
                    token
                |> Async.AwaitTask

            accepted decided |> ignore

            let reader =
                PrincipalKey.human "https://issuer.example/" "summary-reader"
                |> Result.defaultWith (fun _ -> failtest "Valid principal")

            let! registered =
                Service.execute
                    owner
                    (OperationInput.Authority(
                        authorityRequest (id 200) reader AuthorityAct.Register
                    ))
                    token
                |> Async.AwaitTask

            acceptedAuthority registered

            let! granted =
                grant
                    owner
                    (id 201)
                    reader
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseReference "VISIBLE"
                    }
                    true
                |> Async.AwaitTask

            acceptedAuthority granted
            let caller = core fixture reader

            let! result =
                Service.list caller (Inputs.caseListPage { AfterCursor = None; Limit = 50 }) token
                |> Async.AwaitTask

            let rows =
                match result with
                | Outcome.Succeeded page -> page.Items
                | _ -> failtest "Scoped reader can list assigned cases"

            Expect.equal
                (rows |> List.map (fun row -> CaseReference.format row.Reference))
                [ "VISIBLE" ]
                "Summary rows preserve case-scoped disclosure"

            for row in rows do
                let! current =
                    Service.get
                        caller
                        {
                            CaseReference = CaseReference.format row.Reference
                        }
                        token
                    |> Async.AwaitTask

                let current =
                    match current with
                    | Outcome.Succeeded value -> value
                    | _ -> failtest "Every disclosed row must be readable by the same actor"

                Expect.equal row.Revision current.Snapshot.Revision "Revision disclosure agrees"
                let summary = CaseState.fields row.State |> Option.get
                let fields = CaseState.fields current.Snapshot.State |> Option.get
                Expect.equal summary.Status fields.Status "Status comes from the disclosed snapshot"

                Expect.equal
                    summary.ClaimantName
                    (Registration.claimant fields.Registration)
                    "Claimant is already disclosed by case.get"

                Expect.equal
                    summary.InsurerName
                    (Registration.insurer fields.Registration)
                    "Insurer is already disclosed by case.get"

                Expect.equal
                    summary.Claimed
                    (Registration.claimed fields.Registration)
                    "Claimed money is already disclosed"

                Expect.equal
                    summary.Payable
                    (fields.Decision |> Option.map Decision.payable)
                    "Payable money is already disclosed"

                Expect.equal
                    summary.Progress
                    (CaseProgress.ofPayment fields.Progress)
                    "Progress follows the disclosed groups"

                let! history =
                    Service.history
                        caller
                        {
                            CaseReference = CaseReference.format row.Reference
                            AfterCursor = None
                            Limit = 50
                            Detail = HistoryDetail.Summary
                        }
                        token
                    |> Async.AwaitTask

                let entries =
                    match history with
                    | Outcome.Succeeded value -> value.Entries
                    | _ -> failtest "Reader has summary history"

                match List.head entries with
                | HistoryEntry.SummaryEntry entry ->
                    Expect.equal
                        row.ChangedAt
                        entry.RecordedAt
                        "Last change is the latest accepted case entry"
                | _ -> failtest "Requested summary history"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync
        "case summaries preserve scoped business disclosure and ledger change time"
        (summaries ())
