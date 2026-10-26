module ClaimCore.Tests.StewardshipTests

open ClaimCore.Contract

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private request revision action = actRequest revision (action)

let private current revision state =
    let original = Case.snapshot (opened ())

    Case.ofAccepted
        { original with
            Revision = revision
            State = CaseState.map (fun () -> viewFields original) state
        }
    |> accepted

let private transitions =
    [
        (CaseState.Active()), CaseAct.Void "Recorded in error", Ok(CaseState.Voided())
        (CaseState.Voided()), CaseAct.Void "Recorded in error", Error Refusal.AlreadyVoided
        (CaseState.Active()), CaseAct.Reinstate "Reference confirmed", Error Refusal.NotVoided
        (CaseState.Voided()), CaseAct.Reinstate "Reference confirmed", Ok(CaseState.Active())
        (CaseState.Active()), CaseAct.Erase, Ok CaseState.Erased
        (CaseState.Voided()), CaseAct.Erase, Ok CaseState.Erased
    ]

let private invalidReasons =
    [
        Unchecked.defaultof<string>
        ""
        " "
        " leading"
        "trailing "
        "a\u0000b"
        "a b"
        "a b"
        String([| char 0xD800 |])
        String.replicate 201 "x"
    ]

let private revisionProperty =
    property {
        let! revision = Gen.int64 (Range.constant 1L (Revision.Maximum - 1L))
        let! state = Gen.item [ (CaseState.Active()); (CaseState.Voided()) ]

        let result =
            Case.decide today (request revision CaseAct.Erase) (Some(current revision state))

        return
            result
            |> Result.map Case.snapshot
            |> Result.exists (fun next ->
                next.Revision = revision + 1L && next.State = CaseState.Erased)
    }

let private transitionTest () =
    for state, action, expected in transitions do
        let before = current 7L state

        let result =
            Case.decide today (request 7L action) (Some before) |> Result.map Case.snapshot

        let state =
            result
            |> Result.map (fun snapshot ->
                Expect.equal snapshot.Revision 8L "Exactly one revision"

                match CaseState.fields snapshot.State with
                | None -> ()
                | Some fields ->
                    Expect.equal
                        fields
                        (viewFields (Case.snapshot before))
                        "Stewardship preserves business fields"

                CaseState.map ignore snapshot.State)

        Expect.equal state expected "Independent stewardship expectation"

let private reasonTest () =
    for reason in invalidReasons do
        for state, action in
            [
                (CaseState.Active()), CaseAct.Void reason
                (CaseState.Voided()), CaseAct.Reinstate reason
            ] do
            Expect.isError
                (Case.decide today (request 7L action) (Some(current 7L state)))
                "Malformed or newly nonconforming reason refuses"

let private reasonBounds () =
    for reason in [ "Reason"; String.replicate 200 "x"; String.replicate 200 "😀" ] do
        Expect.isOk
            (Case.decide
                today
                (request 7L (CaseAct.Void reason))
                (Some(current 7L (CaseState.Active()))))
            "Exactly 200 Unicode scalars accepted"

    Expect.isError
        (Case.decide
            today
            (request 7L (CaseAct.Void(String.replicate 201 "😀")))
            (Some(current 7L (CaseState.Active()))))
        "201 Unicode scalars refuse"

let private staleRevision () =
    let outcome =
        Case.decide
            today
            (request 6L (CaseAct.Void " leading"))
            (Some(current 7L (CaseState.Voided())))

    Expect.isTrue
        ((outcome |> Result.map Case.snapshot) = Error(Refusal.RevisionConflict 7L))
        "Revision check precedes admission and state"

let private exhausted () =
    let outcome =
        Case.decide
            today
            (request Revision.Maximum CaseAct.Erase)
            (Some(current Revision.Maximum (CaseState.Active())))

    Expect.isTrue
        ((outcome |> Result.map Case.snapshot) = Error Refusal.RevisionExhausted)
        "No overflow at terminal revision"

let private vocabulary () =
    for action, expected in
        [
            CaseAct.Void "Reason", (CaseActKind.Stewardship StewardshipActKind.Void)
            CaseAct.Reinstate "Reason", (CaseActKind.Stewardship StewardshipActKind.Reinstate)
            CaseAct.Erase, (CaseActKind.Stewardship StewardshipActKind.Erase)
        ] do
        Expect.equal
            (CaseActs.kind (action))
            expected
            "Stewardship remains distinct from business acts"

[<Tests>]
let tests =
    testList
        "case stewardship"
        [
            testCase
                "[CC-LIFE-001] every stewardship transition matches its independent expectation"
                transitionTest
            testCase
                "[CC-LIFE-001] reasons reject null malformed Unicode and unsafe text"
                reasonTest
            testCase
                "[CC-LIFE-001] reason bounds count Unicode scalars without rewriting them"
                reasonBounds
            testCase
                "[CC-LIFE-001] stale revision is refused before admission and state"
                staleRevision
            testCase "[CC-LIFE-001] exhausted revisions cannot overflow or accept an act" exhausted
            testCase
                "[CC-LIFE-001] accepted erasure advances exactly one revision across the full range"
                (fun () -> PropertyHarness.run "CC-PROP-STEWARDSHIP-001" revisionProperty)
            testCase "stewardship action kinds retain their separate vocabulary" vocabulary
        ]
