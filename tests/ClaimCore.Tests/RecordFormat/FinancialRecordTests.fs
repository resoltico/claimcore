module ClaimCore.Tests.FinancialRecordTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.RecordFormat

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private snapshot =
    """{"caseId":"019aa6a0-0000-7000-8000-000000000001","revision":3,"state":{"kind":"ACTIVE","fields":{"caseReference":"FIN-1","status":"OPENED","registration":{"incidentDate":"2026-01-01","incidentNotificationDate":"2026-01-02","incidentCountry":"LV","claimantName":"Claimant","insurerName":"Insurer","claimedAmount":"100","claimedCurrency":"EUR"},"finance":{"decision":{"paymentDecisionDate":"2026-01-10","payableAmount":"10","payableCurrency":"EUR"},"paidAmount":"0","paymentCount":0,"firstPaymentDate":null,"lastPaymentDate":null}}}}"""

[<Tests>]
let tests =
    testList
        "bounded financial retained records"
        [
            testCase
                "independently authored aggregate snapshot has no complete payment collection"
                (fun () ->
                    let value = CaseRecord.decodeSnapshot (bytes snapshot) |> accepted
                    let fields = CaseState.fields value.State |> Option.get
                    Expect.equal fields.Finance.PaymentCount 0L "Exact authored count"

                    Expect.equal
                        (CaseProgress.ofFinancial fields.Finance)
                        CaseProgress.Decided
                        "Derived progress"

                    Expect.isNull
                        (typeof<CaseFields>.GetProperty "Payments")
                        "A case snapshot cannot carry the entire entry set"

                    let encoded = CaseRecord.encodeSnapshot value

                    let canonical =
                        StrictJson.read Compartment.MaximumContentBytes (bytes snapshot)
                        |> accepted
                        |> CanonicalWriter.bytes

                    Expect.sequenceEqual encoded canonical "Retained bytes use canonical framing")
            testCase "contradictory financial facts and old collection members refuse" (fun () ->
                for altered in
                    [
                        snapshot.Replace("\"paymentCount\":0", "\"paymentCount\":1")
                        snapshot.Replace("\"paidAmount\":\"0\"", "\"paidAmount\":\"11\"")
                        snapshot.Replace("\"finance\":", "\"payments\":[],\"finance\":")
                    ] do
                    Expect.isError
                        (CaseRecord.decodeSnapshot (bytes altered))
                        "No repair or previous-format path")
        ]
