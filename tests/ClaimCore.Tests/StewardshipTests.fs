module ClaimCore.Tests.StewardshipTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private request revision action =
    actRequest revision (CaseAct.Stewardship action)

let private current revision disposition =
    let original = Case.snapshot (opened ())

    Case.ofAccepted
        { original with
            Revision = revision
            View = CaseView.Live(viewFields original, disposition)
        }
    |> accepted

let private transitions =
    [
        Disposition.Active, StewardshipAct.Void "Recorded in error", Ok(Some Disposition.Voided)
        Disposition.Voided, StewardshipAct.Void "Recorded in error", Error CaseRefusal.AlreadyVoided
        Disposition.Active,
        StewardshipAct.Reinstate "Reference confirmed",
        Error CaseRefusal.NotVoided
        Disposition.Voided,
        StewardshipAct.Reinstate "Reference confirmed",
        Ok(Some Disposition.Active)
        Disposition.Active, StewardshipAct.Erase, Ok None
        Disposition.Voided, StewardshipAct.Erase, Ok None
    ]

let private invalidReasons =
    [
        Unchecked.defaultof<string>
        ""
        " "
        " leading"
        "trailing "
        "a\u0000b"
        "a\u2028b"
        "a\u2029b"
        String([| char 0xD800 |])
        String.replicate 201 "x"
    ]

let private revisionProperty =
    property {
        let! revision = Gen.int64 (Range.constant 1L (Revision.Maximum - 1L))
        let! disposition = Gen.item [ Disposition.Active; Disposition.Voided ]

        let result =
            Case.decide
                today
                (request revision StewardshipAct.Erase)
                (Some(current revision disposition))

        return
            result
            |> Result.map Case.snapshot
            |> Result.exists (fun next ->
                next.Revision = revision + 1L && next.View = CaseView.Erased)
    }

let private transitionTest () =
    for disposition, action, expected in transitions do
        let before = current 7L disposition

        let result =
            Case.decide today (request 7L action) (Some before) |> Result.map Case.snapshot

        let state =
            result
            |> Result.map (fun snapshot ->
                Expect.equal snapshot.Revision 8L "Exactly one revision"

                match snapshot.View with
                | CaseView.Erased -> None
                | CaseView.Live(fields, disposition) ->
                    Expect.isTrue
                        (fields = viewFields (Case.snapshot before))
                        "Stewardship preserves business fields"

                    Some disposition)

        Expect.equal state expected "Independent stewardship expectation"

let private reasonTest () =
    for reason in invalidReasons do
        for disposition, action in
            [
                Disposition.Active, StewardshipAct.Void reason
                Disposition.Voided, StewardshipAct.Reinstate reason
            ] do
            Expect.isError
                (Case.decide today (request 7L action) (Some(current 7L disposition)))
                "Malformed or newly nonconforming reason refuses"

let private reasonBounds () =
    for reason in [ "Reason"; String.replicate 200 "x"; String.replicate 200 "😀" ] do
        Expect.isOk
            (Case.decide
                today
                (request 7L (StewardshipAct.Void reason))
                (Some(current 7L Disposition.Active)))
            "Exactly 200 Unicode scalars accepted"

    Expect.isError
        (Case.decide
            today
            (request 7L (StewardshipAct.Void(String.replicate 201 "😀")))
            (Some(current 7L Disposition.Active)))
        "201 Unicode scalars refuse"

let private staleRevision () =
    let outcome =
        Case.decide
            today
            (request 6L (StewardshipAct.Void " leading"))
            (Some(current 7L Disposition.Voided))

    Expect.isTrue
        ((outcome |> Result.map Case.snapshot) = Error(CaseRefusal.RevisionConflict 7L))
        "Revision check precedes admission and disposition"

let private exhausted () =
    let outcome =
        Case.decide
            today
            (request Revision.Maximum StewardshipAct.Erase)
            (Some(current Revision.Maximum Disposition.Active))

    Expect.isTrue
        ((outcome |> Result.map Case.snapshot) = Error CaseRefusal.RevisionExhausted)
        "No overflow at terminal revision"

let private vocabulary () =
    for action, expected in
        [
            StewardshipAct.Void "Reason", (CaseActKind.Stewardship StewardshipActKind.Void)
            StewardshipAct.Reinstate "Reason",
            (CaseActKind.Stewardship StewardshipActKind.Reinstate)
            StewardshipAct.Erase, (CaseActKind.Stewardship StewardshipActKind.Erase)
        ] do
        Expect.equal
            (CaseActs.kind (CaseAct.Stewardship action))
            expected
            "Stewardship remains distinct from business commands"

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
                "[CC-LIFE-001] stale revision is refused before admission and disposition"
                staleRevision
            testCase "[CC-LIFE-001] exhausted revisions cannot overflow or accept an act" exhausted
            testCase
                "[CC-LIFE-001] accepted erasure advances exactly one revision across the full range"
                (fun () -> PropertyHarness.run "CC-PROP-STEWARDSHIP-001" revisionProperty)
            testCase "stewardship action kinds retain their separate vocabulary" vocabulary
        ]
