module ClaimCore.Tests.CaseSnapshotCodecTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private snapshot state = CaseFamily.snapshot state |> Option.get

[<Tests>]
let tests =
    testList
        "case snapshot semantic admission"
        [
            testCase "snapshot decoding cannot invent a decision for payment" (fun () ->
                let original = snapshot (opened ())
                let bytes = Codec.encode ResponseCodecs.caseSnapshot original
                let root = System.Text.Json.Nodes.JsonNode.Parse(bytes) |> nonNull
                let fields = root["state"] |> nonNull |> fun state -> state["fields"] |> nonNull

                let finance = fields["finance"] |> nonNull
                finance["paidAmount"] <- System.Text.Json.Nodes.JsonValue.Create "1"
                finance["paymentCount"] <- System.Text.Json.Nodes.JsonValue.Create 1L
                finance["firstPaymentDate"] <- System.Text.Json.Nodes.JsonValue.Create "2026-08-20"
                finance["lastPaymentDate"] <- System.Text.Json.Nodes.JsonValue.Create "2026-08-20"

                let invalid = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())

                Expect.isError
                    (Codec.decode 100000 ResponseCodecs.caseSnapshot invalid)
                    "Semantic decoding refuses payment facts with no decision")
            testCase "all six partial decision objects are rejected at the wire boundary" (fun () ->
                let text value = JsonValue.String value

                let registration =
                    (ResponseCodecs.caseFields.Write(viewFields (snapshot (opened ()))))
                    |> function
                        | JsonValue.Object members ->
                            members |> List.find (fst >> (=) "registration") |> snd
                        | _ -> failtest "Expected case field object"

                let candidate mask =
                    let decision =
                        JsonValue.Object
                            [
                                if mask &&& 1 <> 0 then
                                    "paymentDecisionDate", text "2026-08-15"
                                if mask &&& 2 <> 0 then
                                    "payableAmount", text "750"
                                if mask &&& 4 <> 0 then
                                    "payableCurrency", text "EUR"
                            ]

                    let finance =
                        JsonValue.Object
                            [
                                "decision", decision
                                "paidAmount", text "0"
                                "paymentCount", JsonValue.Integer 0L
                                "firstPaymentDate", JsonValue.Null
                                "lastPaymentDate", JsonValue.Null
                            ]

                    JsonValue.Object
                        [
                            "caseReference", text "UNIT-001"
                            "status", text "OPENED"
                            "registration", registration
                            "finance", finance
                        ]

                Expect.isOk
                    (ResponseCodecs.caseFields.Read "$" (candidate 7))
                    "The complete independent control admits"

                for mask in 1..6 do
                    Expect.isError
                        (ResponseCodecs.caseFields.Read "$" (candidate mask))
                        "A decision object requires all three members")
        ]
