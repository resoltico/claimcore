module ClaimCore.Tests.VerificationFoldTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.FoldRecords

let private id = OperationId.value (operation 1)
let private human = principal
let private accepted = required

let private finding expected result =
    match result with
    | Error issue -> Expect.equal issue.Finding expected "Specific verification finding"
    | Ok _ -> failtestf "Expected verification finding %A" expected

let private append state record =
    Replay.append installation CaseReferencePolicy.CustomAllowed None state record

let private initial () =
    let root, _ = owner ()
    root, (append (Replay.initial None) root |> required)

let private rewrite (record: RetainedOperation) header changes =
    RetainedOperation.create
        header
        record.Root.Structure.Task
        record.Root.Structure.Binding
        (record.Inputs |> Option.get)
        changes
        (fun () -> Array.create 32 199uy)
    |> required

let private opened () =
    let root, state = initial ()

    let record, _ =
        build
            state.Fold
            2
            principal
            "case.open"
            (Binding.CaseNew Revision.zero)
            (opening "VERIFY-1")

    root, state, record

let private invalidProjectionRows () =
    let row: Actor =
        {
            ActorId = (CaseFixtures.actorId id)
            Principal = human
            Enabled = true
            Profile = None
        }

    Expect.isNone (ActorId.create Guid.Empty) "Empty projected actor IDs cannot be admitted"
    Expect.isNone (CaseId.create Guid.Empty) "Empty projected grant scopes cannot be admitted"

    let expected = Map.ofList [ row.ActorId, row ]

    let key =
        (row.ActorId,
         {
             ActorGrant.Role = Role.CaseReader
             Scope = Scope.Installation
         })

    let grant = key, (EffectPosition.create Sequence.first 0 |> Option.get)
    let expectedGrant = Map.ofList [ key, grant ]

    let checks =
        [
            (fun () ->
                ProjectionVerification.rows
                    VerificationFinding.PeopleProjection
                    (fun (actor: Actor) -> actor.ActorId)
                    expected
                    [ row; row ])
            (fun () ->
                ProjectionVerification.rows
                    VerificationFinding.PeopleProjection
                    (fun (actor: Actor) -> actor.ActorId)
                    expected
                    [
                        { row with
                            ActorId =
                                CaseFixtures.actorId (
                                    Guid.Parse "20000000-0000-7000-8000-000000000002"
                                )
                        }
                    ])
            (fun () ->
                ProjectionVerification.rows
                    VerificationFinding.PeopleProjection
                    fst
                    Map.empty
                    [ grant ])
            (fun () ->
                ProjectionVerification.rows
                    VerificationFinding.PeopleProjection
                    fst
                    expectedGrant
                    [ grant; grant ])
        ]

    for check in checks do
        match check () with
        | Error {
                    Sequence = None
                    Finding = VerificationFinding.PeopleProjection
                    Attachment = None
                } -> ()
        | _ -> failtest "Malformed projections must return the stable people finding"

[<Tests>]
let tests =
    testList
        "pure operation verification findings"
        [
            testCase
                "ledger link, head and checkpoint checks have independent negative controls"
                (fun () ->
                    let root, state = initial ()

                    Expect.isOk
                        (Replay.link
                            state
                            (Sequence.successor state.Head.Sequence)
                            (EntryHash.toBytes state.Head.EntryHash))
                        "Valid extension"

                    finding
                        VerificationFinding.Link
                        (Replay.link
                            state
                            state.Head.Sequence
                            (EntryHash.toBytes state.Head.EntryHash))

                    Expect.isOk (Replay.head state (Some state.Head)) "Exact head"
                    finding VerificationFinding.Head (Replay.head state None)

                    finding
                        VerificationFinding.ExpectedPosition
                        (Replay.complete
                            (Some
                                {
                                    Sequence = Sequence.first
                                    EntryHash = EntryHash.zero
                                })
                            (Replay.initial None))

                    let expected = Some root.Root.Position

                    let witnessed =
                        Replay.append
                            installation
                            CaseReferencePolicy.CustomAllowed
                            expected
                            (Replay.initial expected)
                            root
                        |> required

                    Expect.equal
                        (Replay.complete expected witnessed)
                        (Ok root.Root.Position)
                        "The expected position was observed")
            testCase
                "installation, operation identity and nonce reuse are independently rejected"
                (fun () ->
                    let root, _ = initial ()

                    finding
                        VerificationFinding.PayloadFormat
                        (Replay.append
                            Guid.Empty
                            CaseReferencePolicy.CustomAllowed
                            None
                            (Replay.initial None)
                            root)

                    let state = Replay.initial None

                    let duplicate =
                        { state with
                            Fold =
                                { state.Fold with
                                    Accepted = Set.singleton root.Root.Header.OperationId
                                }
                        }

                    finding VerificationFinding.PayloadFormat (append duplicate root)

                    let nonce =
                        root.Root.Structure.Compartments.Head
                        |> Compartment.content
                        |> Option.get
                        |> fst
                        |> Convert.ToHexString

                    let reused =
                        { state with
                            Fold =
                                { state.Fold with
                                    Nonces = Set.singleton nonce
                                }
                        }

                    finding VerificationFinding.Commitment (append reused root))
            testCase
                "unknown task identity remains a fold finding after structural hash validation"
                (fun () ->
                    let record, _ = initial ()
                    let root = record.Root

                    let unknown =
                        OperationRoot.append
                            root.Header
                            (TaskId.ofLiteral "people.unknown")
                            root.Structure.Binding
                            root.Structure.Retained
                            root.Structure.Effects
                            root.Structure.Compartments
                        |> required
                        |> RetainedOperation.restore
                        |> required

                    finding
                        VerificationFinding.PayloadFormat
                        (append (Replay.initial None) unknown))
            testCase
                "a correctly hash-committed OPEN with a CLOSED result is not reproduced"
                (fun () ->
                    let _, state, record = opened ()
                    Expect.isOk (append state record) "The genuine accepted decision passes"

                    let changed =
                        record.Changes
                        |> List.map (fun change ->
                            match change.Value with
                            | PartValue.Case value ->
                                { change with
                                    Value =
                                        PartValue.Case
                                            { value with
                                                Handling =
                                                    Handling.Closed
                                                        {
                                                            Outcome = ClosureOutcome.Settled
                                                            Note = None
                                                        }
                                            }
                                }
                            | _ -> failtest "Expected the case part")

                    let forged = rewrite record record.Root.Header changed

                    Expect.isOk
                        (OperationRoot.restore
                            forged.Root.Header
                            (TaskId.token forged.Root.Structure.Task)
                            (OperationRoot.structureBytes forged.Root)
                            (OperationRoot.effectBytes forged.Root)
                            forged.Root.Structure.Compartments
                            forged.Root.Hash)
                        "The negative control reaches decision verification with a valid hash"

                    finding VerificationFinding.StateSequence (append state forged))
            testCase
                "a revision gap and a non-creation first part cannot become accepted history"
                (fun () ->
                    let _, state, record = opened ()

                    let gap =
                        rewrite
                            record
                            record.Root.Header
                            (record.Changes
                             |> List.map (fun part ->
                                 { part with
                                     Revision = Revision.create 2L |> Option.get
                                 }))

                    finding VerificationFinding.RevisionSequence (append state gap)

                    Expect.equal
                        (CaseFamily.apply
                            None
                            { record.Changes.Head with
                                Revision = Revision.zero
                            })
                        (Error RecordViolation.InvalidHistoryStart)
                        "The aggregate independently rejects the invalid first version")
            testCase
                "missing payload requires erasure, and an accepted erase requires earlier content cleared"
                (fun () ->
                    let _, before, record = opened ()
                    let missing = append before (clear record) |> required
                    finding VerificationFinding.PayloadWithoutErasure (Replay.erasure missing)
                    let available = append before record |> required

                    let family =
                        available.Fold.Families[CaseId.ofOperationId record.Root.Header.OperationId]

                    let erase, _ =
                        build
                            available.Fold
                            3
                            principal
                            "case.erase"
                            (Binding.CaseErase(
                                CaseSelector.Id family.Id,
                                family.Case.Revision,
                                family.Sequence
                            ))
                            InputMap.empty

                    finding VerificationFinding.ErasureIncomplete (append available erase)
                    let complete = append missing erase |> required

                    Expect.isOk
                        (Replay.erasure complete)
                        "The actual clearing frontier is justified by the accepted erase")
            testCase
                "family projection membership and each independent index are verified"
                (fun () ->
                    let _, before, record = opened ()
                    let state = append before record |> required
                    let expected = Replay.caseRecords (Replay.familyRows state) |> required

                    Expect.isOk
                        (ProjectionVerification.rows
                            VerificationFinding.CaseProjection
                            (fun (row: StoredCase) -> row.CaseId)
                            expected
                            (expected |> Map.values |> Seq.toList))
                        "Exact rows"

                    finding
                        VerificationFinding.CaseProjection
                        (ProjectionVerification.rows
                            VerificationFinding.CaseProjection
                            (fun (row: StoredCase) -> row.CaseId)
                            expected
                            [])

                    for expected, run in
                        ClaimCore.Tests.ProjectionIndexChecks.checks
                            installation
                            record.Root.Header.RecordedAt do
                        finding expected (run ()))
            testCase
                "invalid projection rows return typed findings without escaping exceptions"
                invalidProjectionRows
        ]
