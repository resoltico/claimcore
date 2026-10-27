module ClaimCore.Tests.VerificationFoldTests

open ClaimCore.TestSupport
open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
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
        ActorId = id
        Principal = principal
        Enabled = true
        Grants = grants
    }

let private owner =
    actor
        human
        [
            {
                Role = Role.Owner
                Scope = GrantScope.Installation
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
            Sequence = 1L
            PreviousHash = Array.zeroCreate 32
            OperationId = id
            ActorId = id
            RecordedAt = instant
            PayloadCommitment = commitment
            Subject =
                RecordedTarget.Authority
                    {
                        TargetActorId = id
                        Act = AuthorityKind.ProvisionOwner
                    }
        }

    let entry: StoredEntry =
        {
            Sequence = 1L
            EntryHash = LedgerEntry.hash metadata
            OperationId = id
            ActorId = id
            RecordedAt = instant
            Subject = EntryTarget.Authority(id, AuthorityKind.ProvisionOwner)
            PayloadCommitment = commitment
            PayloadNonce = Some nonce
            Payload = Some payload
        }

    entry, metadata

let private history: CaseHistory =
    {
        Revision = 1L
        Snapshot = None
        Reference = Some(CaseFixtures.reference "SYNTHETIC")
        State = CaseState.Active()
        MissingPayloadAt = None
        PresentPayloadAt = None
        ErasedAt = None
    }

let private ledgerChecks =
    [
        VerificationFinding.Link, fun () -> LedgerVerification.link state 2L state.Head.EntryHash
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
                        Sequence = 1L
                        EntryHash = Array.zeroCreate 32
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
        fun () -> CaseFold.apply Map.empty entry id 2L opened VerifiedPayload.Erased |> unitResult
        VerificationFinding.HistoryStart,
        fun () -> CaseFold.apply Map.empty entry id 1L closed VerifiedPayload.Erased |> unitResult
        VerificationFinding.StateSequence,
        fun () ->
            CaseFold.apply
                (Map.ofList
                    [
                        id,
                        { history with
                            State = CaseState.Erased
                        }
                    ])
                entry
                id
                2L
                closed
                VerifiedPayload.Erased
            |> unitResult
        VerificationFinding.ErasureIncomplete,
        fun () ->
            CaseFold.erasure (
                Map.ofList
                    [
                        id,
                        { history with
                            State = CaseState.Erased
                            PresentPayloadAt = Some 1L
                            ErasedAt = Some 2L
                        }
                    ]
            )
        VerificationFinding.PayloadWithoutErasure,
        fun () ->
            CaseFold.erasure (
                Map.ofList
                    [
                        id,
                        { history with
                            MissingPayloadAt = Some 1L
                        }
                    ]
            )
        VerificationFinding.CaseProjection,
        fun () -> ProjectionVerification.cases installation (Map.ofList [ id, history ]) []
    ]

let private authorityChecks =
    let entry, _ = row [| 1uy |]

    let executor actors human owner =
        AuthorityFold.executor (Map.ofList [ id, actors ]) entry human owner

    [
        VerificationFinding.ActorNotEnabled,
        fun () -> AuthorityFold.executor Map.empty entry false false
        VerificationFinding.HumanRequired, fun () -> executor (actor service []) true false
        VerificationFinding.OwnerRequired, fun () -> executor (actor human []) true true
        VerificationFinding.AuthorityFold,
        fun () ->
            let entry =
                { entry with
                    Subject = EntryTarget.Authority(id, AuthorityKind.Register)
                }

            let request: AuthorityRequestRecord =
                {
                    OperationId = id
                    Target = PrincipalKey.describe human
                    Act = RecordedAuthorityAct.Operation(ClaimCore.Domain.AuthorityAct.Register)
                }

            AuthorityFold.apply
                installation
                (fun _ -> false)
                (Map.ofList [ id, owner ])
                entry
                request
            |> unitResult
        VerificationFinding.AuthorityProjection,
        fun () -> ProjectionVerification.authority (Map.ofList [ id, owner ]) [] []
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
                            Target = PrincipalKey.describe human
                            Act = RecordedAuthorityAct.ProvisionOwner
                        }
                Snapshot = Array.empty
            }

    let entry, metadata = row bytes
    let duplicate = LedgerVerification.initial None (Some 1L)

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
        (append (LedgerVerification.initial None (Some 2L)) entry metadata)
        "A later duplicate cannot hide an earlier valid row"

let private invalidProjectionRows () =
    let row: Actor =
        {
            ActorId = id
            Principal = human
            Enabled = true
        }

    let actors = Map.ofList [ id, owner ]

    let duplicateGrant =
        { owner with
            Grants =
                [
                    {
                        Role = Role.CaseReader
                        Scope = GrantScope.Installation
                    }
                ]
        }

    let checks =
        [
            (fun () -> ProjectionVerification.actor Map.empty { row with ActorId = Guid.Empty })
            (fun () -> ProjectionVerification.actor actors row)
            (fun () ->
                ProjectionVerification.actor
                    actors
                    { row with
                        ActorId = Guid.Parse "20000000-0000-4000-8000-000000000002"
                    })
            (fun () -> ProjectionVerification.grant Map.empty (id, Role.CaseReader, None))
            (fun () ->
                ProjectionVerification.grant
                    (Map.ofList[id, duplicateGrant])
                    (id, Role.CaseReader, None))
            (fun () -> ProjectionVerification.grant actors (id, Role.CaseReader, Some Guid.Empty))
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
    let cases = ledgerChecks @ caseChecks @ authorityChecks

    testList
        "pure verification findings"
        [
            testCase "duplicate prepass preserves per-row precedence" duplicatePrecedence
            testCase "every finding has a pure rejection case" (fun () ->
                Expect.equal
                    (cases |> List.map fst |> Set.ofList)
                    (VerificationFindings.all |> List.map fst |> Set.ofList)
                    "Complete vocabulary")
            for expected, run in cases do
                testCase (VerificationFindings.token expected) (fun () -> finding expected run)
        ]

[<Tests>]
let projectionTests =
    testCase
        "invalid projection rows return typed findings without escaping exceptions"
        invalidProjectionRows
