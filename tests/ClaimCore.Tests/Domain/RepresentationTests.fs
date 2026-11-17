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
                    let raw = System.Guid.Parse "91000000-0000-7000-8000-000000000001"

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
            testCase "financial position cannot construct payments without a decision" (fun () ->
                Expect.isError
                    (FinancialPosition.create
                        None
                        (Amount.parse "1" |> Result.defaultWith (string >> failtest))
                        1L
                        (PaymentDateBounds.create
                            (DomainInputs.date "2026-08-20")
                            (DomainInputs.date "2026-08-20")))
                    "Invalid state is rejected at construction"

                Expect.equal
                    (FinancialPosition.create
                        None
                        (Amount.parse "0" |> Result.defaultWith (string >> failtest))
                        0L
                        None)
                    (Ok FinancialPosition.undecided)
                    "No accepted financial facts is undecided")
        ]
