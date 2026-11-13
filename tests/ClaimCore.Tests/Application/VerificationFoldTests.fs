module ClaimCore.Tests.VerificationFoldTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private id =
    OperationIds.numberedAt (DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)) 1

let private installation = Guid.Parse "90000000-0000-7000-8000-000000000001"
let private instant = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)

let private human =
    PrincipalKey.human "https://issuer.example/" "synthetic-owner" |> accepted

let private service =
    PrincipalKey.service "https://issuer.example/" "synthetic-service" |> accepted

let private actor principal grants : ActorAccess =
    {
        Actor =
            {
                ActorId = CaseFixtures.actorId id
                Principal = principal
                Enabled = true
                Profile = None
            }
        Grants = grants
    }

let private owner =
    actor
        human
        [
            {
                Role = Role.Owner
                Scope = Scope.Installation
            }
        ]

let private unitResult result = Result.map (fun _ -> ()) result
let private state = Replay.initial None

let private header sequence operation actor previous businessDate =
    {
        OperationHeader.InstallationId = installation
        Sequence = CaseFixtures.sequence sequence
        PreviousHash = previous
        OperationId = CaseFixtures.operationId operation
        ActorId = actor
        RecordedAt = instant
        BusinessDate = businessDate
    }

let private nonce value () = Array.create 32 value

let private root () =
    RetainedOperation.create
        (header 1L id (CaseFixtures.actorId id) EntryHash.zero None)
        (CommandSkeleton.People
            {
                TargetActorId = CaseFixtures.actorId id
                Principal = human
                Act = PeopleAct.ProvisionOwner(0, People.initialOwnerRoles)
            })
        (OperationValues.People
            {
                Profile = Some(ActorProfile.parse Inputs.actorProfile |> accepted)
            })
        (nonce 1uy)
    |> accepted

let private entry () =
    root () |> StoredEffect.ofOperation |> List.head

let private finding expected run =
    match run () with
    | Error divergence -> Expect.equal divergence.Finding expected "Stable finding category"
    | Ok() -> failtest "Corrupt synthetic state was accepted"

let private append state root =
    Replay.append installation None state root

// The root is structurally valid and correctly committed; replay must independently decide its state.
let private caseRoot revision kind afterStatus =
    let command =
        Case.parse
            {
                OperationId = id
                CaseReference = "EFFECT-OPEN"
                ExpectedRevision = revision
                Act = kind
            }
        |> accepted

    let reference = CaseFixtures.reference "EFFECT-OPEN"

    let snapshot =
        {
            CaseSnapshot.CaseId = CaseFixtures.caseId id
            Reference = reference
            Revision = CaseFixtures.revision (revision + 1L)
            State =
                CaseState.Active
                    {
                        CaseReference = reference
                        Status = afterStatus
                        Registration = Inputs.admittedRegistration Inputs.registration
                        Finance = FinancialPosition.undecided
                    }
        }

    RetainedOperation.create
        (header 1L id (CaseFixtures.actorId id) EntryHash.zero (Some(DateOnly(2026, 9, 7))))
        (CommandSkeleton.Case
            {
                CaseId = snapshot.CaseId
                Reference = reference
                ExpectedRevision = command.ExpectedRevision
                Act = CaseAct.kind kind
                SelectedPayment = None
            })
        (OperationValues.Case
            {
                Command = command
                Decision = { After = snapshot; Payment = None }
            })
        (nonce 2uy)


let private caseEntry revision kind status =
    caseRoot revision kind status
    |> accepted
    |> StoredEffect.ofOperation
    |> List.head

[<Tests>]
let requestEffectAgreement =
    testCase "verification rejects a correctly committed OPEN with a CLOSED result" (fun () ->
        let check status =
            let root = caseRoot 0L (CaseAct.Open Inputs.registration) status |> accepted

            let executor =
                { owner with
                    Grants =
                        {
                            Role = Role.CaseHandler
                            Scope = Scope.Installation
                        }
                        :: owner.Grants
                }

            append
                { state with
                    People =
                        Map.ofList
                            [
                                owner.ActorId,
                                {
                                    PeopleSubjectState.Actor = executor.Actor
                                    Grants =
                                        executor.Grants
                                        |> List.mapi (fun index grant ->
                                            grant,
                                            EffectPosition.create Sequence.first index
                                            |> Option.get)
                                        |> Map.ofList
                                    ProfileErasure =
                                        Some(EffectPosition.create Sequence.first 1 |> Option.get)
                                }
                            ]
                }
                root
            |> unitResult

        Expect.isOk (check CaseStatus.Opened) "Independently authored OPEN result passes"
        finding VerificationFinding.StateSequence (fun () -> check CaseStatus.Closed))

let private history: CaseHistory =
    {
        PaymentProjections = Map.empty
        Revision = CaseFixtures.revision 1L
        ChangedAt = instant
        Snapshot = None
        Reference = Some(CaseFixtures.reference "SYNTHETIC")
        State = CaseState.Active()
        Status = CaseStatus.Opened
        MissingPayloadAt = None
        PresentPayloadAt = None
        ErasedAt = None
    }

let private ledgerChecks =
    [
        VerificationFinding.Link,
        (fun () ->
            Replay.link state (CaseFixtures.sequence 2L) (EntryHash.toBytes state.Head.EntryHash))
        VerificationFinding.Head, (fun () -> Replay.head state None)
        VerificationFinding.ExpectedPosition,
        (fun () ->
            Replay.complete
                (Some
                    {
                        Sequence = Sequence.first
                        EntryHash = EntryHash.zero
                    })
                state
            |> unitResult)
        VerificationFinding.PayloadFormat,
        (fun () ->
            Replay.append (Guid.Parse "90000000-0000-7000-8000-000000000099") None state (root ())
            |> unitResult)
        VerificationFinding.PayloadFormat,
        (fun () ->
            let root = root ()

            append
                { state with
                    Accepted = Map.ofList [ root.OperationId, root.ActorId ]
                }
                root
            |> unitResult)
        VerificationFinding.Commitment,
        (fun () ->
            let root = root ()

            append
                { state with
                    AvailableNonces = Set.singleton (Convert.ToHexString(nonce 1uy ()))
                }
                root
            |> unitResult)
    ]

let private caseChecks =
    [
        VerificationFinding.RevisionSequence,
        (fun () ->
            SubjectReplay.caseEffect
                Map.empty
                (caseEntry 1L (CaseAct.Bare BareAct.Close) CaseStatus.Closed)
                VerifiedPayload.Erased
            |> unitResult)
        VerificationFinding.HistoryStart,
        (fun () ->
            SubjectReplay.caseEffect
                (Map.ofList
                    [
                        CaseFixtures.caseId id,
                        { history with
                            Revision = Revision.zero
                        }
                    ])
                (caseEntry 0L (CaseAct.Open Inputs.registration) CaseStatus.Opened)
                VerifiedPayload.Erased
            |> unitResult)
        VerificationFinding.StateSequence,
        (fun () ->
            SubjectReplay.caseEffect
                (Map.ofList
                    [
                        CaseFixtures.caseId id,
                        { history with
                            State = CaseState.Erased
                        }
                    ])
                (caseEntry 1L (CaseAct.Bare BareAct.Close) CaseStatus.Closed)
                VerifiedPayload.Erased
            |> unitResult)
        VerificationFinding.ErasureIncomplete,
        (fun () ->
            SubjectReplay.caseErasure (
                Map.ofList
                    [
                        CaseFixtures.caseId id,
                        { history with
                            State = CaseState.Erased
                            PresentPayloadAt = Some Sequence.first
                            ErasedAt = Some(CaseFixtures.sequence 2L)
                        }
                    ]
            ))
        VerificationFinding.PayloadWithoutErasure,
        (fun () ->
            SubjectReplay.caseErasure (
                Map.ofList
                    [
                        CaseFixtures.caseId id,
                        { history with
                            MissingPayloadAt = Some Sequence.first
                        }
                    ]
            ))
        VerificationFinding.CaseProjection,
        (fun () ->
            Replay.caseRecords
                { state with
                    Cases = Map.ofList [ CaseFixtures.caseId id, history ]
                }
            |> Result.bind (fun expected ->
                ProjectionVerification.rows
                    VerificationFinding.CaseProjection
                    (fun (row: StoredCase) -> row.CaseId)
                    expected
                    [])
            |> unitResult)
    ]

let private peopleChecks =
    let executor actor human required =
        ReplayAccess.executor (Map.ofList [ owner.ActorId, actor ]) (entry ()) human required

    [
        VerificationFinding.ActorNotEnabled,
        (fun () -> ReplayAccess.executor Map.empty (entry ()) false false)
        VerificationFinding.HumanRequired, (fun () -> executor (actor service []) true false)
        VerificationFinding.OwnerRequired, (fun () -> executor (actor human []) true true)
        VerificationFinding.PeopleFold,
        (fun () ->
            let root = root ()

            let person =
                {
                    PeopleSubjectState.Actor = owner.Actor
                    Grants =
                        owner.Grants
                        |> List.map (fun grant ->
                            grant, EffectPosition.create Sequence.first 0 |> Option.get)
                        |> Map.ofList
                    ProfileErasure = Some(EffectPosition.create Sequence.first 1 |> Option.get)
                }

            Replay.append
                installation
                None
                { state with
                    People = Map.ofList [ owner.ActorId, person ]
                }
                root
            |> unitResult)
        VerificationFinding.PeopleProjection,
        (fun () ->
            ProjectionVerification.rows
                VerificationFinding.PeopleProjection
                (fun (actor: Actor) -> actor.ActorId)
                (Map.ofList [ owner.ActorId, owner.Actor ])
                [])
        VerificationFinding.PeopleProfileProjection,
        (fun () ->
            ProjectionVerification.rows
                VerificationFinding.PeopleProfileProjection
                fst
                (Map.ofList [ owner.ActorId, (owner.ActorId, None) ])
                [ owner.ActorId, Some(ActorProfile.parse Inputs.actorProfile |> accepted) ])
    ]

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
                } -> ()
        | _ -> failtest "Malformed projections must return the stable people finding"

[<Tests>]
let tests =
    testList
        "pure operation verification findings"
        [
            for index, (expected, run) in
                ledgerChecks
                @ caseChecks
                @ peopleChecks
                @ ProjectionIndexChecks.checks installation instant
                |> List.indexed do
                testCase (string index + " " + VerificationFinding.token expected) (fun () ->
                    finding expected run)
        ]

[<Tests>]
let projectionTests =
    testCase
        "invalid projection rows return typed findings without escaping exceptions"
        invalidProjectionRows

[<Tests>]
let firstRevisionBoundary =
    testCase "a non-OPEN first revision is refused by the root before replay" (fun () ->
        Expect.equal
            (caseRoot 0L (CaseAct.Bare BareAct.Close) CaseStatus.Closed)
            (Error(OperationContentProblem.Structure OperationStructureProblem.Command))
            "A closed typed root cannot construct the retired invalid effect")
