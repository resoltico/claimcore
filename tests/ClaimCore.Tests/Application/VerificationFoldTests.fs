module ClaimCore.Tests.VerificationFoldTests

open ClaimCore.TestSupport
open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.Tests.Fixtures

let private id = Guid.Parse "a0000000-0000-4000-8000-000000000001"
let private installation = Guid.Parse "90000000-0000-4000-8000-000000000001"
let private instant = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)

let private human =
    PrincipalKey.human "https://issuer.example/" "synthetic-owner" |> accepted

let private service =
    PrincipalKey.service "https://issuer.example/" "synthetic-service" |> accepted

let private actor principal grants : ActorAuthority =
    {
        Actor =
            {
                ActorId = (CaseFixtures.actorId id)
                Principal = principal
                Enabled = true
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

let private state = LedgerVerification.initial None None
let private unitResult result = Result.map (fun _ -> ()) result

let private row payload =
    let nonce = Array.create 32 1uy
    let commitment = LedgerEntry.commitment nonce payload

    let metadata: LedgerMetadata =
        {
            InstallationId = installation
            Sequence = CaseFixtures.sequence 1L
            PreviousHash = Array.zeroCreate 32
            OperationId = (CaseFixtures.operationId id)
            ActorId = (CaseFixtures.actorId id)
            RecordedAt = instant
            PayloadCommitment = commitment
            Subject =
                RecordedTarget.Authority
                    {
                        TargetActorId = (CaseFixtures.actorId id)
                        Act = AuthorityKind.ProvisionOwner
                    }
        }

    let entry: StoredEntry =
        {
            Sequence = CaseFixtures.sequence 1L
            EntryHash = LedgerEntry.hash metadata
            OperationId = (CaseFixtures.operationId id)
            ActorId = (CaseFixtures.actorId id)
            RecordedAt = instant
            Subject = EntryTarget.Authority((CaseFixtures.actorId id), AuthorityKind.ProvisionOwner)
            PayloadCommitment = commitment
            PayloadNonce = Some nonce
            Payload = Some payload
        }

    entry, metadata

[<Tests>]
let requestEffectAgreement =
    testCase "verification rejects a rehashed OPEN with a CLOSED result" (fun () ->
        let request =
            CaseFixtures.command
                {
                    OperationId = id
                    CaseReference = "EFFECT-OPEN"
                    ExpectedRevision = 0L
                    Act = AuthoredCaseAct.Open Inputs.registration
                }

        let reference = CaseFixtures.reference "EFFECT-OPEN"

        let expected: CaseSnapshot =
            {
                Reference = reference
                Revision = Revision.first
                State =
                    CaseState.Active
                        {
                            CaseReference = reference
                            Status = CaseStatus.Opened
                            Registration = Inputs.admittedRegistration Inputs.registration
                            Progress = PaymentProgress.Undecided
                        }
            }

        let check snapshot =
            let payload =
                LedgerEntry.encodePayload
                    {
                        Request = RequestRecord.encode request
                        Snapshot = CaseRecord.encodeSnapshot snapshot
                    }

            let entry, metadata = row payload
            let caseId = CaseReferenceId.encode installation "EFFECT-OPEN"

            let metadata =
                { metadata with
                    Subject =
                        RecordedTarget.Case
                            {
                                CaseId = caseId
                                Revision = Revision.first
                                Act = CaseActKind.Open
                                BusinessDate = DateOnly(2026, 9, 7)
                            }
                }

            let entry =
                { entry with
                    Subject =
                        EntryTarget.Case(
                            caseId,
                            Revision.first,
                            CaseActKind.Open,
                            DateOnly(2026, 9, 7)
                        )
                    EntryHash = LedgerEntry.hash metadata
                }

            LedgerVerification.append
                installation
                None
                { state with
                    Actors = Map.ofList [ CaseFixtures.actorId id, owner ]
                }
                entry
                metadata

        Expect.isOk (check expected) "An independently authored OPEN result passes"
        let fields = CaseState.fields expected.State |> Option.get

        let forged =
            { expected with
                State =
                    CaseState.Active
                        { fields with
                            Status = CaseStatus.Closed
                        }
            }

        Expect.equal
            (check forged |> Result.map ignore)
            (Error
                {
                    Sequence = Some Sequence.first
                    Finding = VerificationFinding.StateSequence
                })
            "Correct commitments and hashes do not prove request effect agreement")

let private history: CaseHistory =
    {
        Revision = CaseFixtures.revision 1L
        ChangedAt = instant
        Snapshot = None
        Reference = Some(CaseFixtures.reference "SYNTHETIC")
        State = CaseState.Active()
        MissingPayloadAt = None
        PresentPayloadAt = None
        ErasedAt = None
    }

let private ledgerChecks =
    [
        VerificationFinding.Link,
        fun () ->
            LedgerVerification.link
                state
                (CaseFixtures.sequence 2L)
                (EntryHash.toBytes state.Head.EntryHash)
        VerificationFinding.EntryHash,
        fun () ->
            let entry, metadata = row [| 1uy |]

            LedgerVerification.append
                installation
                None
                state
                { entry with
                    EntryHash = Array.zeroCreate 32
                }
                metadata
            |> unitResult
        VerificationFinding.Commitment,
        fun () ->
            let entry, metadata = row [| 1uy |]

            LedgerVerification.append
                installation
                None
                state
                { entry with
                    PayloadNonce = Some(Array.zeroCreate 32)
                }
                metadata
            |> unitResult
        VerificationFinding.PayloadFormat,
        fun () ->
            let entry, metadata = row [| 1uy |]
            LedgerVerification.append installation None state entry metadata |> unitResult
        VerificationFinding.Head, fun () -> LedgerVerification.head state None
        VerificationFinding.ExpectedPosition,
        fun () ->
            LedgerVerification.complete
                (Some
                    {
                        Sequence = CaseFixtures.sequence 1L
                        EntryHash = EntryHash.zero
                    })
                state
            |> unitResult
    ]

let private caseChecks =
    let entry, _ = row [| 1uy |]
    let opened = CaseActKind.Open
    let closed = CaseActKind.Close

    [
        VerificationFinding.RevisionSequence,
        fun () ->
            CaseFold.apply
                Map.empty
                { entry with
                    Subject =
                        EntryTarget.Case(
                            (CaseFixtures.caseId id),
                            (CaseFixtures.revision 2L),
                            opened,
                            DateOnly.FromDateTime instant.DateTime
                        )
                }
                VerifiedPayload.Erased
            |> unitResult
        VerificationFinding.HistoryStart,
        fun () ->
            CaseFold.apply
                Map.empty
                { entry with
                    Subject =
                        EntryTarget.Case(
                            (CaseFixtures.caseId id),
                            (CaseFixtures.revision 1L),
                            closed,
                            DateOnly.FromDateTime instant.DateTime
                        )
                }
                VerifiedPayload.Erased
            |> unitResult
        VerificationFinding.StateSequence,
        fun () ->
            CaseFold.apply
                (Map.ofList
                    [
                        (CaseFixtures.caseId id),
                        { history with
                            State = CaseState.Erased
                        }
                    ])
                { entry with
                    Subject =
                        EntryTarget.Case(
                            (CaseFixtures.caseId id),
                            (CaseFixtures.revision 2L),
                            closed,
                            DateOnly.FromDateTime instant.DateTime
                        )
                }
                VerifiedPayload.Erased
            |> unitResult
        VerificationFinding.ErasureIncomplete,
        fun () ->
            CaseFold.erasure (
                Map.ofList
                    [
                        CaseFixtures.caseId id,
                        { history with
                            State = CaseState.Erased
                            PresentPayloadAt = Some(CaseFixtures.sequence 1L)
                            ErasedAt = Some(CaseFixtures.sequence 2L)
                        }
                    ]
            )
        VerificationFinding.PayloadWithoutErasure,
        fun () ->
            CaseFold.erasure (
                Map.ofList
                    [
                        CaseFixtures.caseId id,
                        { history with
                            MissingPayloadAt = Some(CaseFixtures.sequence 1L)
                        }
                    ]
            )
        VerificationFinding.CaseProjection,
        fun () ->
            ProjectionVerification.cases
                installation
                (Map.ofList [ (CaseFixtures.caseId id), history ])
                []
    ]

let private authorityChecks =
    let entry, _ = row [| 1uy |]

    let executor actors human owner =
        AuthorityFold.executor (Map.ofList [ (CaseFixtures.actorId id), actors ]) entry human owner

    [
        VerificationFinding.ActorNotEnabled,
        fun () -> AuthorityFold.executor Map.empty entry false false
        VerificationFinding.HumanRequired, fun () -> executor (actor service []) true false
        VerificationFinding.OwnerRequired, fun () -> executor (actor human []) true true
        VerificationFinding.AuthorityFold,
        fun () ->
            let entry =
                { entry with
                    Subject =
                        EntryTarget.Authority((CaseFixtures.actorId id), AuthorityKind.Register)
                }

            let request: AuthorityRequestRecord =
                {
                    OperationId = id
                    Target = PrincipalKey.toInput human
                    Act = ClaimCore.Domain.AuthorityAct.Register
                }

            AuthorityFold.apply
                installation
                (fun _ -> false)
                (Map.ofList [ (CaseFixtures.actorId id), owner ])
                entry
                request
            |> unitResult
        VerificationFinding.AuthorityProjection,
        fun () ->
            ProjectionVerification.authority (Map.ofList [ (CaseFixtures.actorId id), owner ]) [] []
    ]

let private finding expected run =
    match run () with
    | Error divergence ->
        Expect.equal divergence.Finding expected "Stable finding token and precedence"
    | Ok() -> failtest "Corrupt synthetic input was accepted"

let private duplicatePrecedence () =
    let bytes =
        LedgerEntry.encodePayload
            {
                Request =
                    AuthorityRecord.encode
                        {
                            OperationId = id
                            Target = PrincipalKey.toInput human
                            Act = AuthorityAct.ProvisionOwner
                        }
                Snapshot = Array.empty
            }

    let entry, metadata = row bytes
    let duplicate = LedgerVerification.initial None (Some(CaseFixtures.sequence 1L))

    let append state entry metadata =
        LedgerVerification.append installation None state entry metadata |> unitResult

    Expect.isOk (append state entry metadata) "Independent valid bootstrap control"
    finding VerificationFinding.PayloadFormat (fun () -> append duplicate entry metadata)

    finding VerificationFinding.EntryHash (fun () ->
        append
            duplicate
            { entry with
                EntryHash = Array.zeroCreate 32
            }
            metadata)

    finding VerificationFinding.Commitment (fun () ->
        append
            duplicate
            { entry with
                PayloadNonce = Some(Array.zeroCreate 32)
            }
            metadata)

    Expect.isOk
        (append (LedgerVerification.initial None (Some(CaseFixtures.sequence 2L))) entry metadata)
        "A later duplicate cannot hide an earlier valid row"

let private invalidProjectionRows () =
    let row: Actor =
        {
            ActorId = (CaseFixtures.actorId id)
            Principal = human
            Enabled = true
        }

    let actors = Map.ofList [ CaseFixtures.actorId id, owner ]

    let duplicateGrant =
        { owner with
            Grants =
                [
                    {
                        Role = Role.CaseReader
                        Scope = Scope.Installation
                    }
                ]
        }

    Expect.isNone (ActorId.create Guid.Empty) "Empty projected actor IDs cannot be admitted"
    Expect.isNone (CaseId.create Guid.Empty) "Empty projected grant scopes cannot be admitted"

    let checks =
        [
            (fun () -> ProjectionVerification.actor actors row)
            (fun () ->
                ProjectionVerification.actor
                    actors
                    { row with
                        ActorId =
                            (CaseFixtures.actorId (
                                Guid.Parse "20000000-0000-4000-8000-000000000002"
                            ))
                    })
            (fun () ->
                ProjectionVerification.grant
                    Map.empty
                    ((CaseFixtures.actorId id), Role.CaseReader, None))
            (fun () ->
                ProjectionVerification.grant
                    (Map.ofList[(CaseFixtures.actorId id), duplicateGrant])
                    ((CaseFixtures.actorId id), Role.CaseReader, None))
        ]

    for check in checks do
        match check () with
        | Error {
                    Sequence = None
                    Finding = VerificationFinding.AuthorityProjection
                } -> ()
        | _ -> failtest "Malformed projections must return the stable authority finding"

[<Tests>]
let tests =
    let cases =
        ledgerChecks
        @ caseChecks
        @ authorityChecks
        @ (ProjectionIndexChecks.checks installation instant)

    testList
        "pure verification findings"
        [
            testCase "duplicate prepass preserves per-row precedence" duplicatePrecedence
            testCase "every finding has a pure rejection case" (fun () ->
                Expect.equal
                    (cases |> List.map fst |> Set.ofList)
                    (VerificationFinding.all |> List.map fst |> Set.ofList)
                    "Complete vocabulary")
            for expected, run in cases do
                testCase (VerificationFinding.token expected) (fun () -> finding expected run)
        ]

[<Tests>]
let projectionTests =
    testCase
        "invalid projection rows return typed findings without escaping exceptions"
        invalidProjectionRows
