module ClaimCore.Tests.RepresentationTests

open Expecto
open ClaimCore.Domain

[<Tests>]
let tests =
    testList
        "admitted representations"
        [
            testCase "entry hashes own their bytes and reject invalid widths" (fun () ->
                let bytes = Array.create 32 7uy
                let hash = EntryHash.ofBytes bytes |> Option.get
                bytes[0] <- 9uy
                let output = EntryHash.toBytes hash
                Expect.equal output[0] 7uy "Caller mutation cannot alter an admitted hash"
                output[0] <- 3uy

                Expect.equal
                    ((EntryHash.toBytes hash)[0])
                    7uy
                    "Returned arrays cannot alter the hash"

                for length in [ 0; 31; 33 ] do
                    Expect.isNone
                        (EntryHash.ofBytes (Array.zeroCreate length))
                        "A hash has exactly 32 bytes")
            testCase "ledger positions include genesis and enforce exact-integer bounds" (fun () ->
                Expect.equal
                    (LedgerPosition.create 0L EntryHash.zero)
                    (Some LedgerPosition.genesis)
                    "Genesis is an admitted empty head"

                Expect.isNone
                    (LedgerPosition.create -1L EntryHash.zero)
                    "Negative sequence refused"

                Expect.isSome
                    (LedgerPosition.create SafeInteger.Maximum EntryHash.zero)
                    "Exact upper bound admitted"

                Expect.isNone
                    (LedgerPosition.create (SafeInteger.Maximum + 1L) EntryHash.zero)
                    "Unsafe integer refused")
            testCase "payment groups cannot construct paid without a decision" (fun () ->
                let date =
                    CalendarDate.parse "2026-08-20" |> Result.defaultWith (string >> failwith)

                Expect.isError
                    (PaymentProgress.fromGroups None (Some date))
                    "Invalid state is rejected at construction"

                Expect.equal
                    (PaymentProgress.fromGroups None None)
                    (Ok PaymentProgress.Undecided)
                    "No authored groups is undecided")
        ]

[<Tests>]
let progressWire =
    testCase "all payment states retain independent decision/payment wire members" (fun () ->
        let decision =
            ClaimCore.TestSupport.CaseFixtures.decision ClaimCore.TestSupport.Inputs.decision

        let date =
            CalendarDate.parse "2026-08-20" |> Result.defaultWith (string >> failwith)

        for progress, decided, paid in
            [
                PaymentProgress.Undecided, false, false
                PaymentProgress.Decided decision, true, false
                PaymentProgress.Paid(decision, date), true, true
            ] do
            let fields: CaseFields =
                {
                    CaseReference = ClaimCore.TestSupport.CaseFixtures.reference "WIRE-STATES"
                    Status = CaseStatus.Opened
                    Registration =
                        ClaimCore.TestSupport.CaseFixtures.registration
                            ClaimCore.TestSupport.Inputs.registration
                    Progress = progress
                }

            let codec = ClaimCore.Protocol.ResponseCodecs.caseFields
            let value = codec.Write fields

            Expect.equal
                (ClaimCore.Json.JsonValue.tryMember "decision" value
                 <> Some ClaimCore.Json.JsonValue.Null)
                decided
                "Decision is present exactly in decided/paid states"

            Expect.equal
                (ClaimCore.Json.JsonValue.tryMember "payment" value
                 <> Some ClaimCore.Json.JsonValue.Null)
                paid
                "Payment is present only in paid state"

            Expect.equal
                (codec.Read "$" value)
                (Ok fields)
                "All three states decode without losing their admitted structure")
