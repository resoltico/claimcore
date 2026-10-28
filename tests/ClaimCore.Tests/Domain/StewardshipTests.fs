module ClaimCore.Tests.StewardshipTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

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
        (CaseState.Active()), AuthoredCaseAct.Void "Recorded in error", Ok(CaseState.Voided())
        (CaseState.Voided()),
        AuthoredCaseAct.Void "Recorded in error",
        Error(Refusal.Case CaseRefusal.AlreadyVoided)
        (CaseState.Active()),
        AuthoredCaseAct.Reinstate "Reference confirmed",
        Error(Refusal.Case CaseRefusal.NotVoided)
        (CaseState.Voided()),
        AuthoredCaseAct.Reinstate "Reference confirmed",
        Ok(CaseState.Active())
        (CaseState.Active()), AuthoredCaseAct.Erase, Ok CaseState.Erased
        (CaseState.Voided()), AuthoredCaseAct.Erase, Ok CaseState.Erased
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
            CaseFixtures.decide
                today
                (request revision AuthoredCaseAct.Erase)
                (Some(current revision state))

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
            CaseFixtures.decide today (request 7L action) (Some before)
            |> Result.map Case.snapshot

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
                (CaseState.Active()), AuthoredCaseAct.Void reason
                (CaseState.Voided()), AuthoredCaseAct.Reinstate reason
            ] do
            Expect.isError
                (CaseFixtures.decide today (request 7L action) (Some(current 7L state)))
                "Malformed or newly nonconforming reason refuses"

let private reasonBounds () =
    for reason in [ "Reason"; String.replicate 200 "x"; String.replicate 200 "😀" ] do
        Expect.isOk
            (CaseFixtures.decide
                today
                (request 7L (AuthoredCaseAct.Void reason))
                (Some(current 7L (CaseState.Active()))))
            "Exactly 200 Unicode scalars accepted"

    Expect.isError
        (CaseFixtures.decide
            today
            (request 7L (AuthoredCaseAct.Void(String.replicate 201 "😀")))
            (Some(current 7L (CaseState.Active()))))
        "201 Unicode scalars refuse"

let private staleRevision () =
    let outcome =
        CaseFixtures.decide
            today
            (request 6L (AuthoredCaseAct.Void " leading"))
            (Some(current 7L (CaseState.Voided())))

    Expect.isTrue
        ((outcome |> Result.map Case.snapshot) = Error(Refusal.RevisionConflict 7L))
        "Revision check precedes admission and state"

let private vocabulary () =
    for action, expected in
        [
            AuthoredCaseAct.Void "Reason", CaseActKind.Void
            AuthoredCaseAct.Reinstate "Reason", CaseActKind.Reinstate
            AuthoredCaseAct.Erase, CaseActKind.Erase
        ] do
        Expect.equal
            (CaseAct.kind (action))
            expected
            "Stewardship remains distinct from business acts"

[<Tests>]
let tests =
    testList
        "case stewardship"
        [
            testCase
                "every stewardship transition matches its independent expectation"
                transitionTest
            testCase "reasons reject null malformed Unicode and unsafe text" reasonTest
            testCase "reason bounds count Unicode scalars without rewriting them" reasonBounds
            testCase "stale revision is refused before admission and state" staleRevision
            testCase
                "accepted erasure advances exactly one revision across the full range"
                (fun () -> PropertyHarness.run "stewardship revision" revisionProperty)
            testCase "stewardship action kinds retain their separate vocabulary" vocabulary
        ]
