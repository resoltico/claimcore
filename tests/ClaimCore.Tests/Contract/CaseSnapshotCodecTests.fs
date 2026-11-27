module ClaimCore.Tests.CaseSnapshotCodecTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport
open ClaimCore.Tests.CaseBuilders

let private snapshot state = CaseFamily.snapshot state |> Option.get

[<Tests>]
let tests =
    testList
        "case snapshot semantic admission"
        [
            testCase "snapshot decoding preserves payments without a decision" (fun () ->
                let original = snapshot (opened ())
                let bytes = Codec.encode ResponseCodecs.caseSnapshot original
                let root = System.Text.Json.Nodes.JsonNode.Parse(bytes) |> nonNull
                let fields = root["state"] |> nonNull |> fun state -> state["fields"] |> nonNull

                let finance = fields["finance"] |> nonNull

                finance["totals"] <-
                    System.Text.Json.Nodes.JsonNode.Parse(
                        """[{"currency":"USD","paid":"1","returned":"0"}]"""
                    )

                finance["paymentCount"] <- System.Text.Json.Nodes.JsonValue.Create 1L

                let invalid = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())

                Expect.isOk
                    (Codec.decode 100000 ResponseCodecs.caseSnapshot invalid)
                    "Recorded payments need no decision")
            testCase "closed retained handling requires a recorded outcome" (fun () ->
                let bytes (value: string) =
                    System.Text.Encoding.UTF8.GetBytes value

                let decode value =
                    Codec.decode 4096 ClaimCore.RecordFormat.HandlingCodecs.value (bytes value)

                Expect.isError
                    (decode """{"kind":"CLOSED"}""")
                    "A closure without an outcome is refused"

                Expect.isOk
                    (decode """{"kind":"CLOSED","outcome":"DUPLICATE"}""")
                    "Positive independent control")
            testCase "all six partial decision objects are rejected at the wire boundary" (fun () ->
                let text value = JsonValue.String value

                let registration = JsonValue.Object []

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
                                "totals", JsonValue.Array []
                                "paymentCount", JsonValue.Integer 0L
                            ]

                    JsonValue.Object
                        [
                            "caseReference", text "CC-ZQ16-FT6R-54T8-E39K-67"
                            "handling", JsonValue.Object [ "kind", text "OPEN" ]
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
