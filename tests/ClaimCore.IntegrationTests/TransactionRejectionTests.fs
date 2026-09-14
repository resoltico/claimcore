module ClaimCore.IntegrationTests.TransactionRejectionTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private caseDigest claim =
    claim
    |> Claim.view
    |> CaseRecord.encodeSnapshot
    |> SHA256.HashData
    |> Convert.ToHexStringLower

let private historyIdentity (page: HistoryPage) =
    page.Items
    |> List.map (fun (receipt: Receipt) ->
        receipt.OperationId,
        receipt.RecordedAt,
        receipt.RecordedBy,
        receipt.Replayed,
        receipt.CommandName,
        caseDigest receipt.Case),
    page.NextAfterVersion

let private requireTypedDecisionRejection (initial: CommandRequest) =
    use runtime =
        Runtime.OpenPostgres(appConnection (), CancellationToken.None)
        |> await
        |> Result.defaultWith (fun _ -> failtest "Synthetic runtime must open.")

    let request: ClaimCore.Domain.CommandRequest =
        {
            OperationId = Guid.NewGuid()
            CaseReference = initial.CaseReference
            ExpectedVersion = 1L
            Command = Command.RecordPayment "2026-08-20"
        }

    match runtime.Core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.RejectedBeforeAttempt(None, rejection) when
        rejection.Code = RejectionCode.DecisionRequired
        ->
        ()
    | _ -> failtest "The typed core must return a definite decision-required rejection."

let tests =
    testList
        "rejected transactions"
        [
            testCase "[CC-APP-001] rejected command changes neither case nor history" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let initial = newRequest ()
                Service.executeAsync service clock initial |> await |> accepted |> ignore

                let beforeCase = service.Get(initial.CaseReference) |> await |> accepted

                let beforeHistory = service.History(initial.CaseReference, 0L) |> await |> accepted

                let result =
                    Service.executeAsync
                        service
                        clock
                        (next initial 1L (Command.RecordPayment "2026-08-20"))
                    |> await

                match result with
                | Error(CoreFailure.Domain DomainError.DecisionRequired) -> ()
                | _ -> failtest "A payment without decision must receive the Domain refusal."

                requireTypedDecisionRejection initial

                let afterCase = service.Get(initial.CaseReference) |> await |> accepted

                let afterHistory = service.History(initial.CaseReference, 0L) |> await |> accepted

                Expect.equal
                    (afterCase |> Option.map caseDigest)
                    (beforeCase |> Option.map caseDigest)
                    "Current business snapshot is unchanged"

                Expect.equal
                    (historyIdentity afterHistory)
                    (historyIdentity beforeHistory)
                    "Accepted history identity and snapshot digests are unchanged"

                Expect.equal afterHistory.Items.Length 1 "No accepted-operation row for rejection")
        ]
