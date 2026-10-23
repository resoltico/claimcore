module ClaimCore.Tests.VerificationFoldTests

open ClaimCore.Contract

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

let private state = LedgerVerification.initial None
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
            Scope =
                LedgerScope.Authority
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
            Scope = StoredScope.Authority(id, AuthorityKind.ProvisionOwner)
            PayloadCommitment = commitment
            PayloadNonce = Some nonce
            Payload = Some payload
        }

    entry, metadata

let private history: CaseHistory =
    {
        Revision = 1L
        Snapshot = None
        Reference = Some "SYNTHETIC"
        Disposition = Disposition.Active
        Erased = false
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
    let opened = CaseActKind.Business BusinessActKind.Open
    let closed = CaseActKind.Business BusinessActKind.Close

    [
        VerificationFinding.RevisionSequence,
        fun () -> CaseFold.apply Map.empty entry id 2L opened VerifiedPayload.Erased |> unitResult
        VerificationFinding.HistoryStart,
        fun () -> CaseFold.apply Map.empty entry id 1L closed VerifiedPayload.Erased |> unitResult
        VerificationFinding.DispositionSequence,
        fun () ->
            CaseFold.apply
                (Map.ofList [ id, { history with Erased = true } ])
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
                            Erased = true
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
                    Scope = StoredScope.Authority(id, AuthorityKind.RegisterActor)
                }

            let request: AuthorityRequestRecord =
                {
                    OperationId = id
                    Target = PrincipalKey.describe human
                    Act =
                        RecordedAuthorityAct.Operation(
                            ClaimCore.Contract.AuthorityAct.RegisterActor
                        )
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

[<Tests>]
let tests =
    let cases = ledgerChecks @ caseChecks @ authorityChecks

    testList
        "pure verification findings"
        [
            testCase "every finding has a pure rejection case" (fun () ->
                Expect.equal
                    (cases |> List.map fst |> Set.ofList)
                    (VerificationFindings.all |> List.map fst |> Set.ofList)
                    "Complete vocabulary")
            for expected, run in cases do
                testCase (VerificationFindings.token expected) (fun () -> finding expected run)
        ]
