module ClaimCore.IntegrationTests.ManagedCopySignerTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopySignerAssertions

let private assertSignerAudit owner app witness keyId =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let verified = DataAudit.run audit witness CancellationToken.None |> await
    Expect.equal verified.SignerApprovals 4L "Both human pairs were audited."
    Expect.equal verified.SignerKeys 1L "One retained signer was audited."
    Expect.equal verified.SignerEvents 2L "Register and retire were replayed."

    use ownerConnection = new NpgsqlConnection(owner)
    ownerConnection.Open()

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copy_signers SET event_hash=decode(repeat('ff',32),'hex') "
            + "WHERE signing_key_id=@key",
            ownerConnection
        )

    tamper.Parameters.AddWithValue("key", keyId) |> ignore
    Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic signer tip was changed."

    Expect.throwsT<InvalidDataException>
        (fun () -> DataAudit.run audit witness CancellationToken.None |> await |> ignore)
        "A primary-owner signer projection rewrite cannot pass the full audit."

let private refuseSubMicrosecondApproval
    (runtime: Runtime)
    custodian
    (witness: WitnessProtocol)
    keyId
    digest
    =
    let request =
        approval
            keyId
            digest
            CopySignerAction.Register
            CopySignerPurpose.CopyAttestor
            CopySignerApprovalRole.Custodian

    let unaligned =
        { request with
            ExpiresAt = request.ExpiresAt.AddTicks(1L)
        }

    let before = witness.Snapshot().TipSequence

    Expect.equal
        ((runtime.ForActor custodian).ApproveCopySigner(unaligned, CancellationToken.None)
         |> await)
        CopySignerApprovalOutcome.ResourceUnavailable
        "Sub-microsecond expiry cannot create an unusable witnessed approval."

    Expect.equal
        (witness.Snapshot().TipSequence)
        before
        "Invalid expiry creates no witness authority."

let private dualHumanRoster =
    testCase
        "[CC-BACKUP-001] two authenticated human approvals register and retire one witnessed signer"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let ownerPrincipal = human "copy-owner"
                let custodian = human "copy-custodian"
                provision owner witness ownerPrincipal |> ActorGrantTestSupport.applied
                use runtime = openRuntime app writer
                grantCustodian runtime ownerPrincipal custodian

                let algorithm = SignatureAlgorithm.Ed25519
                use key = Key.Create(algorithm)
                let raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey)
                let digest = SHA256.HashData(raw)
                let keyId = Guid.NewGuid()

                refuseSubMicrosecondApproval runtime custodian witness keyId digest

                let ownerApproval, custodianApproval =
                    approvePair
                        runtime
                        ownerPrincipal
                        custodian
                        keyId
                        digest
                        CopySignerAction.Register
                        CopySignerPurpose.CopyAttestor

                verifyRoster
                    owner
                    runtime
                    ownerPrincipal
                    custodian
                    witness
                    algorithm
                    keyId
                    digest
                    raw
                    ownerApproval
                    custodianApproval

                assertSignerAudit owner app witness keyId))

let private refusedActors =
    testCase "[CC-BACKUP-001] unknown or service actors cannot approve signer trust" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let ownerPrincipal = human "signer-denial-owner"
            provision owner witness ownerPrincipal |> ActorGrantTestSupport.applied
            use runtime = openRuntime app writer

            let request =
                approval
                    (Guid.NewGuid())
                    (SHA256.HashData(Array.create 32 1uy))
                    CopySignerAction.Register
                    CopySignerPurpose.CopyAttestor
                    (CopySignerApprovalRole.Owner(Guid.NewGuid()))

            Expect.equal
                ((runtime.ForActor(human "unknown-signer"))
                    .ApproveCopySigner(request, CancellationToken.None)
                 |> await)
                CopySignerApprovalOutcome.ResourceUnavailable
                "Unknown human has no ambient signer authority."

            Expect.equal
                ((runtime.ForActor(service "automation-signer"))
                    .ApproveCopySigner(request, CancellationToken.None)
                 |> await)
                CopySignerApprovalOutcome.ResourceUnavailable
                "Service principal cannot impersonate a human approval."))

let private orphanApproval =
    testCase "[CC-BACKUP-001] orphan signer approval intent stays unknown" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let ownerPrincipal = human "orphan-signer-owner"
            let custodian = human "orphan-signer-holder"
            provision owner witness ownerPrincipal |> ActorGrantTestSupport.applied
            use runtime = openRuntime app writer
            grantCustodian runtime ownerPrincipal custodian
            let approvalId = Guid.NewGuid()
            let keyId = Guid.NewGuid()
            let keyDigest = SHA256.HashData(Array.create 32 7uy)

            let holderRequest =
                approval
                    keyId
                    keyDigest
                    CopySignerAction.Register
                    CopySignerPurpose.CopyAttestor
                    CopySignerApprovalRole.Custodian

            (runtime.ForActor custodian).ApproveCopySigner(holderRequest, CancellationToken.None)
            |> await
            |> approved holderRequest.ApprovalId

            let request =
                { approval
                      keyId
                      keyDigest
                      CopySignerAction.Register
                      CopySignerPurpose.CopyAttestor
                      (CopySignerApprovalRole.Owner holderRequest.ApprovalId) with
                    ApprovalId = approvalId
                    ExpiresAt = holderRequest.ExpiresAt
                }

            witness.BeginAuthority(approvalId, [| 0x43uy; 0x43uy; 0x41uy |], None) |> ignore

            for _ in 1..2 do
                Expect.equal
                    ((runtime.ForActor ownerPrincipal)
                        .ApproveCopySigner(request, CancellationToken.None)
                     |> await)
                    (CopySignerApprovalOutcome.StartedUnconfirmed approvalId)
                    "The same witnessed orphan cannot be silently retried."

            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use count =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.managed_copy_signer_approvals "
                    + "WHERE approval_id=@approval",
                    connection
                )

            count.Parameters.AddWithValue("approval", approvalId) |> ignore
            Expect.equal (count.ExecuteScalar() :?> int64) 0L "No primary approval is invented."))

let tests =
    testList "managed-copy signer authority" [ dualHumanRoster; refusedActors; orphanApproval ]
