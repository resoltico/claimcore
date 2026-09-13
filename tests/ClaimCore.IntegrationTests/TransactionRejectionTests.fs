module ClaimCore.IntegrationTests.TransactionRejectionTests

open System
open System.Security.Cryptography
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
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

let tests =
    testList
        "rejected transactions"
        [
            testCase "[CC-APP-001] rejected command changes neither case nor history" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let initial = newRequest ()
                Service.executeAsync service clock initial |> await |> accepted |> ignore

                let beforeCase =
                    Service.getAsync service initial.CaseReference |> await |> accepted

                let beforeHistory =
                    Service.historyAsync service initial.CaseReference 0L |> await |> accepted

                let result =
                    Service.executeAsync
                        service
                        clock
                        (next initial 1L (Command.RecordPayment "2026-08-20"))
                    |> await

                Expect.isError result "No decision"

                let afterCase = Service.getAsync service initial.CaseReference |> await |> accepted

                let afterHistory =
                    Service.historyAsync service initial.CaseReference 0L |> await |> accepted

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
