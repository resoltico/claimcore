module ClaimCore.IntegrationTests.SummaryDisclosureTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
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
                        (ClaimCore.TestSupport.Inputs.caseOperation (
                            request number reference 0L (AuthoredCaseAct.Open Inputs.registration)
                        ))
                        token
                    |> Async.AwaitTask

                accepted result |> ignore

            let! decided =
                Service.execute
                    owner
                    (ClaimCore.TestSupport.Inputs.caseOperation (
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
                        authorityRequest
                            (id 200)
                            reader
                            (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
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
                            Target = CaseSelector.Reference(CaseReference.format row.Reference)
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

                Expect.equal
                    row.Headline
                    current.Headline
                    "The headline is already disclosed by case.get"

                let value field = Map.find field summary.Fields

                Expect.equal
                    (value BusinessField.ClaimantName)
                    (Some(
                        CaseFieldValue.Business(
                            Admitted.Name(Registration.claimant fields.Registration)
                        )
                    ))
                    "Claimant is already disclosed"

                Expect.equal
                    (value BusinessField.InsurerName)
                    (Some(
                        CaseFieldValue.Business(
                            Admitted.Name(Registration.insurer fields.Registration)
                        )
                    ))
                    "Insurer is already disclosed"

                let claimed = Registration.claimed fields.Registration

                Expect.equal
                    (value BusinessField.ClaimedAmount)
                    (Some(CaseFieldValue.Business(Admitted.Amount(Money.amount claimed))))
                    "Claimed amount is already disclosed"

                Expect.equal
                    (value BusinessField.ClaimedCurrency)
                    (Some(CaseFieldValue.Business(Admitted.Currency(Money.currencyValue claimed))))
                    "Claimed currency is already disclosed"

                let payable = fields.Decision |> Option.map Decision.payable

                Expect.equal
                    (value BusinessField.PayableAmount)
                    (payable
                     |> Option.map (Money.amount >> Admitted.Amount >> CaseFieldValue.Business))
                    "Payable amount is already disclosed"

                Expect.equal
                    (value BusinessField.PayableCurrency)
                    (payable
                     |> Option.map (
                         Money.currencyValue >> Admitted.Currency >> CaseFieldValue.Business
                     ))
                    "Payable currency is already disclosed"

                let! history =
                    Service.history
                        caller
                        {
                            Target = CaseSelector.Reference(CaseReference.format row.Reference)
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
