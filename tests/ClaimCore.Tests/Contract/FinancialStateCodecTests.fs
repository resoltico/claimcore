module ClaimCore.Tests.FinancialStateCodecTests

open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

[<Tests>]
let progressWire =
    testCase "all four derived payment states use bounded financial wire facts" (fun () ->
        let decision =
            ClaimCore.TestSupport.CaseFixtures.decision ClaimCore.TestSupport.DomainInputs.decision

        let entry amount =
            CaseFixtures.paymentEntry
                (System.Guid.Parse "10000000-0000-7000-8000-000000000003")
                (DomainInputs.paymentFrom [ BusinessField.PaidAmount, amount ])

        for finance, decided, count in
            [
                FinancialPosition.undecided, false, 0L
                CaseFixtures.finance (Some decision) [], true, 0L
                CaseFixtures.finance (Some decision) [ entry "100" ], true, 1L
                CaseFixtures.finance (Some decision) [ entry "750" ], true, 1L
            ] do
            let fields: CaseFields =
                {
                    CaseReference = ClaimCore.TestSupport.CaseFixtures.reference "WIRE-STATES"
                    Status = CaseStatus.Opened
                    Registration =
                        ClaimCore.TestSupport.CaseFixtures.registration
                            ClaimCore.TestSupport.DomainInputs.registration
                    Finance = finance
                }

            let codec = ClaimCore.Wire.ResponseCodecs.caseFields
            let value = codec.Write fields

            let financial = ClaimCore.Json.JsonValue.tryMember "finance" value |> Option.get

            Expect.equal
                (ClaimCore.Json.JsonValue.tryMember "decision" financial
                 <> Some ClaimCore.Json.JsonValue.Null)
                decided
                "Decision is present exactly in decided/paid states"

            Expect.equal
                (ClaimCore.Json.JsonValue.tryMember "paymentCount" financial)
                (Some(ClaimCore.Json.JsonValue.Integer count))
                "The bounded count is retained"

            for retired in [ "payments"; "progress" ] do
                Expect.isNone
                    (ClaimCore.Json.JsonValue.tryMember retired value)
                    "Full entry arrays and contradictory progress tags are absent"

            Expect.equal
                (codec.Read "$" value)
                (Ok fields)
                "All four states decode without losing their admitted structure")
