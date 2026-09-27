module ClaimCore.IntegrationTests.ManagedCopyIngestTests

open System
open System.Security.Cryptography
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyAuditTestSupport

let private refused =
    function
    | AuthorityWriteOutcome.Refused -> ()
    | _ -> failtest "Invalid managed-copy evidence must be refused before acceptance."

let internal acceptedCopy eventId =
    function
    | AuthorityWriteOutcome.Applied(id, 1L) when id = eventId -> ()
    | AuthorityWriteOutcome.Unconfirmed _ -> failtest "Managed-copy ingest remained unconfirmed."
    | _ -> failtest "Exact managed-copy registration was not accepted."

let private countCopy connection copyId =
    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    command.Parameters.AddWithValue("copy", copyId) |> ignore
    command.ExecuteScalar() :?> int64

let private lacksVerificationProof connection copyId =
    use command =
        new NpgsqlCommand(
            "SELECT verification_proof_sha256 IS NULL FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    command.Parameters.AddWithValue("copy", copyId) |> ignore
    command.ExecuteScalar() :?> bool

let internal registeredSigner runtime ownerPrincipal custodian purpose witness ownerConnection =
    let algorithm = SignatureAlgorithm.Ed25519
    let key = Key.Create(algorithm)
    let raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey)
    let keyId = Guid.NewGuid()
    let digest = SHA256.HashData(raw)

    let ownerApproval, custodianApproval =
        approvePair runtime ownerPrincipal custodian keyId digest CopySignerAction.Register purpose

    let eventId = Guid.NewGuid()

    ManagedCopySignerAdministration.register
        ownerConnection
        witness
        eventId
        keyId
        purpose
        raw
        ownerApproval
        custodianApproval
    |> await
    |> appliedSigner eventId

    key, algorithm, keyId, digest

let private registerVerified
    owner
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    keyId
    =
    let eventId = Guid.NewGuid()
    let copyId = Guid.NewGuid()
    let canonical = registerBase owner (witness.Snapshot()) keyId eventId copyId
    let signature = algorithm.Sign(key, canonical)

    Expect.isTrue
        (ManagedCopyRegistrationAttestation.parse canonical).IsSome
        "Synthetic owner attestation is exact canonical JSON."

    ManagedCopyAdministration.ingest connection witness canonical signature
    |> await
    |> acceptedCopy eventId

    Expect.equal (countCopy connection copyId) 1L "One copy projection is retained."
    Expect.isTrue (lacksVerificationProof connection copyId) "Registration is not verification."

    Expect.isTrue
        (ManagedCopyOwnerInspection.inspect connection witness copyId |> await)
        "Read-only inspection verifies registered key and witness evidence."

    ManagedCopyAdministration.ingest connection witness canonical signature
    |> await
    |> acceptedCopy eventId

    copyId, canonical, signature

let private rejectAltered
    owner
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    canonical
    signature
    =
    let altered = Array.copy signature
    altered[0] <- altered[0] ^^^ 1uy

    ManagedCopyAdministration.ingest connection witness canonical altered
    |> await
    |> refused

    let claimedProof =
        Text.Encoding.ASCII
            .GetString(canonical)
            .Replace(
                "\"verificationProofSha256\":null",
                "\"verificationProofSha256\":\"" + String.replicate 64 "a" + "\"",
                StringComparison.Ordinal
            )
        |> Text.Encoding.ASCII.GetBytes

    Expect.isNone
        (ManagedCopyRegistrationAttestation.parse claimedProof)
        "A REGISTER cannot claim independent verification."

    ManagedCopyAdministration.ingest
        connection
        witness
        claimedProof
        (algorithm.Sign(key, claimedProof))
    |> await
    |> refused

    let unregistered =
        registerBase owner (witness.Snapshot()) (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid())

    ManagedCopyAdministration.ingest
        connection
        witness
        unregistered
        (algorithm.Sign(key, unregistered))
    |> await
    |> refused

let private retireAndReject
    owner
    ownerPrincipal
    custodian
    (runtime: Runtime)
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    keyId
    digest
    copyId
    =
    let retireOwner, retireCustodian =
        approvePair
            runtime
            ownerPrincipal
            custodian
            keyId
            digest
            CopySignerAction.Retire
            CopySignerPurpose.CopyAttestor

    let retireEvent = Guid.NewGuid()

    ManagedCopySignerAdministration.retire
        connection
        witness
        retireEvent
        keyId
        CopySignerPurpose.CopyAttestor
        retireOwner
        retireCustodian
    |> await
    |> appliedSigner retireEvent

    Expect.isTrue
        (ManagedCopyOwnerInspection.inspect connection witness copyId |> await)
        "Retired key remains available to verify old copy proof."

    let newEvent = Guid.NewGuid()
    let newCopy = Guid.NewGuid()
    let newCanonical = registerBase owner (witness.Snapshot()) keyId newEvent newCopy

    ManagedCopyAdministration.ingest
        connection
        witness
        newCanonical
        (algorithm.Sign(key, newCanonical))
    |> await
    |> refused

    Expect.equal (countCopy connection newCopy) 0L "Retired signer cannot add a copy."

let private registeredCopy =
    testCase
        "[CC-BACKUP-001] registered signer alone can ingest exact copy and retirement preserves old proof"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let ownerPrincipal = human "copy-ingest-owner"
                let custodian = human "copy-ingest-custodian"
                provision owner witness ownerPrincipal |> ActorGrantTestSupport.applied
                use runtime = openRuntime app writer
                grantCustodian runtime ownerPrincipal custodian
                use connection = new NpgsqlConnection(owner)
                connection.Open()

                let key, algorithm, keyId, digest =
                    registeredSigner
                        runtime
                        ownerPrincipal
                        custodian
                        CopySignerPurpose.CopyAttestor
                        witness
                        connection

                use key = key

                let copyId, canonical, signature =
                    registerVerified owner connection witness key algorithm keyId

                rejectAltered owner connection witness key algorithm canonical signature

                retireAndReject
                    owner
                    ownerPrincipal
                    custodian
                    runtime
                    connection
                    witness
                    key
                    algorithm
                    keyId
                    digest
                    copyId

                assertOwnerCopyAudit owner app writer witness copyId))

let private orphanCopyIntent =
    testCase
        "[CC-BACKUP-001] orphan managed-copy intent remains unknown without primary row"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let ownerPrincipal = human "orphan-copy-owner"
                let custodian = human "orphan-copy-custodian"
                provision owner witness ownerPrincipal |> ActorGrantTestSupport.applied
                use runtime = openRuntime app writer
                grantCustodian runtime ownerPrincipal custodian
                use connection = new NpgsqlConnection(owner)
                connection.Open()

                let key, algorithm, keyId, _ =
                    registeredSigner
                        runtime
                        ownerPrincipal
                        custodian
                        CopySignerPurpose.CopyAttestor
                        witness
                        connection

                use key = key
                let eventId = Guid.NewGuid()
                let copyId = Guid.NewGuid()
                let canonical = registerBase owner (witness.Snapshot()) keyId eventId copyId
                let signature = algorithm.Sign(key, canonical)
                witness.BeginAuthority(eventId, [| 0x43uy; 0x43uy; 0x55uy |], None) |> ignore

                for _ in 1..2 do
                    match
                        ManagedCopyAdministration.ingest connection witness canonical signature
                        |> await
                    with
                    | AuthorityWriteOutcome.Unconfirmed id when id = eventId -> ()
                    | _ -> failtest "Orphan intent must never become a definite new copy."

                Expect.equal (countCopy connection copyId) 0L "No copy row is invented."))

let tests =
    testList "managed-copy owner ingest" [ registeredCopy; orphanCopyIntent ]
