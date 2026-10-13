module ClaimCore.Tests.StewardshipTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain

let private request revision action =
    {
        OperationId = Guid.Parse "20000000-0000-4000-8000-000000000002"
        CaseReference = "UNIT-001"
        ExpectedVersion = revision
        Action = action
    }

let private transitions =
    [
        CaseDisposition.Active,
        StewardshipAction.Void "Recorded in error",
        Ok(StewardshipResult.Retained(8L, CaseDisposition.Voided))
        CaseDisposition.Voided,
        StewardshipAction.Void "Recorded in error",
        Error StewardshipError.AlreadyVoided
        CaseDisposition.Active,
        StewardshipAction.Reinstate "Reference confirmed",
        Error StewardshipError.NotVoided
        CaseDisposition.Voided,
        StewardshipAction.Reinstate "Reference confirmed",
        Ok(StewardshipResult.Retained(8L, CaseDisposition.Active))
        CaseDisposition.Active, StewardshipAction.Erase, Ok(StewardshipResult.Erased 8L)
        CaseDisposition.Voided, StewardshipAction.Erase, Ok(StewardshipResult.Erased 8L)
    ]

let private invalidReasons =
    [
        Unchecked.defaultof<string>
        ""
        " "
        " leading"
        "trailing "
        "a\u0000b"
        "a\u200Cb"
        "a\u2028b"
        "a\u2029b"
        String([| char 0xD800 |])
        String.replicate 201 "x"
    ]

let private revisionProperty =
    property {
        let! revision = Gen.int64 (Range.constant 1L (Int64.MaxValue - 2L))
        let! disposition = Gen.item [ CaseDisposition.Active; CaseDisposition.Voided ]

        let accepted =
            Stewardship.decide (request revision StewardshipAction.Erase) revision disposition

        return accepted = Ok(StewardshipResult.Erased(revision + 1L))
    }

[<Tests>]
let tests =
    testList
        "case stewardship"
        [
            testCase
                "[CC-LIFE-001] every stewardship transition matches its independent expectation"
                (fun () ->
                    for disposition, action, expected in transitions do
                        Expect.equal
                            (Stewardship.decide (request 7L action) 7L disposition)
                            expected
                            "Transition and refusal")
            testCase
                "[CC-LIFE-001] reasons reject null malformed Unicode and unsafe text"
                (fun () ->
                    for reason in invalidReasons do
                        for action in
                            [ StewardshipAction.Void reason; StewardshipAction.Reinstate reason ] do
                            Expect.equal
                                (Stewardship.decide (request 7L action) 7L CaseDisposition.Active)
                                (Error StewardshipError.InvalidReason)
                                "Reason is refused before disposition")
            testCase
                "[CC-LIFE-001] reason bounds count Unicode scalars without rewriting them"
                (fun () ->
                    for reason in [ "Reason"; String.replicate 200 "x"; String.replicate 200 "😀" ] do
                        Expect.isOk
                            (Stewardship.decide
                                (request 7L (StewardshipAction.Void reason))
                                7L
                                CaseDisposition.Active)
                            "Exactly 200 scalars accepted"

                    Expect.isError
                        (Stewardship.decide
                            (request 7L (StewardshipAction.Void(String.replicate 201 "😀")))
                            7L
                            CaseDisposition.Active)
                        "201 scalars refused")
            testCase
                "[CC-LIFE-001] stale revision is refused before reason and disposition"
                (fun () ->
                    Expect.equal
                        (Stewardship.decide
                            (request 6L (StewardshipAction.Void Unchecked.defaultof<string>))
                            7L
                            CaseDisposition.Voided)
                        (Error(StewardshipError.VersionConflict 7L))
                        "Locked actual revision reported")
            testCase "[CC-LIFE-001] exhausted revisions cannot overflow or accept an act" (fun () ->
                for revision in [ Int64.MaxValue - 1L; Int64.MaxValue ] do
                    Expect.equal
                        (Stewardship.decide
                            (request revision StewardshipAction.Erase)
                            revision
                            CaseDisposition.Active)
                        (Error StewardshipError.RevisionExhausted)
                        "No unrepresentable revision")
            testCase
                "[CC-LIFE-001] accepted erasure advances exactly one revision across the full range"
                (fun () -> PropertyHarness.run "CC-PROP-STEWARDSHIP-001" revisionProperty)
            testCase "stewardship action kinds retain their separate vocabulary" (fun () ->
                Expect.equal
                    (Stewardship.kind (StewardshipAction.Void "Reason"))
                    StewardshipKind.Void
                    "Void"

                Expect.equal
                    (Stewardship.kind (StewardshipAction.Reinstate "Reason"))
                    StewardshipKind.Reinstate
                    "Reinstate"

                Expect.equal
                    (Stewardship.kind StewardshipAction.Erase)
                    StewardshipKind.Erase
                    "Erase")
        ]
