module ClaimCore.Tests.RepresentationTests

open ClaimCore.TestSupport

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
                    (LedgerPosition.create Sequence.zero EntryHash.zero)
                    LedgerPosition.genesis
                    "Genesis is an admitted empty head"

                Expect.isNone (Sequence.create -1L) "Negative sequence refused"

                Expect.isSome (Sequence.create 9007199254740991L) "Exact upper bound admitted"

                Expect.isNone (Sequence.create 9007199254740992L) "Unsafe integer refused")
            testCase
                "identifier constructors reject empty values and preserve valid identities and positions"
                (fun () ->
                    let raw = System.Guid.Parse "91000000-0000-4000-8000-000000000001"

                    for create in
                        [
                            OperationId.create >> Option.map OperationId.value
                            ActorId.create >> Option.map ActorId.value
                            CaseId.create >> Option.map CaseId.value
                        ] do
                        Expect.isNone (create System.Guid.Empty) "Empty identity refused"
                        Expect.equal (create raw) (Some raw) "Identity bits are preserved"

                    for create in
                        [
                            Revision.create >> Option.map Revision.value
                            Sequence.create >> Option.map Sequence.value
                        ] do
                        for value in [ 0L; 1L; 9007199254740991L ] do
                            Expect.equal (create value) (Some value) "Exact position is preserved"

                        for value in [ -1L; 9007199254740992L; System.Int64.MaxValue ] do
                            Expect.isNone (create value) "Unsafe position refused"

                    Expect.throwsT<System.OverflowException>
                        (fun () ->
                            Revision.successor (Revision.create 9007199254740991L |> Option.get)
                            |> ignore)
                        "Revision cannot advance beyond the exact integer bound"

                    Expect.throwsT<System.OverflowException>
                        (fun () ->
                            Sequence.successor (Sequence.create 9007199254740991L |> Option.get)
                            |> ignore)
                        "Sequence cannot advance beyond the exact integer bound")
            testCase "payment groups cannot construct paid without a decision" (fun () ->
                let date =
                    CalendarDate.parse "2026-08-20" |> Result.defaultWith (string >> failwith)

                Expect.isError
                    (PaymentProgress.fromGroups None (Some(Payment.onDate date)))
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
                PaymentProgress.Paid(decision, Payment.onDate date), true, true
            ] do
            let fields: CaseFields =
                {
                    Work = CaseWork.empty
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
