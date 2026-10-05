module internal ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionCompletion

open System.Threading
open System
open System.IO
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Database
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionFixture
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests

let private transition
    (pending: PendingSyntheticCopy)
    (approval: CopyDeletionApprovalRequest)
    tip
    observedAt
    =
    let registeredHash =
        ManagedCopyEventHash.compute
            (Array.zeroCreate<byte> 32)
            pending.Registration
            (Some pending.RegistrationSignature)

    let previous =
        ManagedCopyEventHash.compute
            registeredHash
            pending.DeleteRequest
            (Some pending.DeleteSignature)

    let source =
        transitionFromRegister
            pending.Registration
            approval.DeletionEventId
            3
            "VERIFIED_DELETED"
            "VERIFIED_DELETED"
            previous
            tip

    changed
        source
        [
            "deletionApprovalId", element (approval.ApprovalId.ToString("D"))
            "deletionProofSha256",
            element (Convert.ToHexStringLower(approval.InspectionReportSha256))
            "lastVerifiedAt", element (stamp observedAt)
        ]

let private assertRefused
    (directory: string)
    (custodianId: string)
    (signaturePath: string)
    inputs
    (path: string)
    =
    let code, response =
        runCommand "verify-delete-managed-copy" [ path; signaturePath ] inputs

    use response = response
    Expect.isGreaterThan code 0 "Unsafe copy input is refused before owner commit."

    let safe = response.RootElement.GetRawText()

    Expect.isFalse
        (safe.Contains(directory, StringComparison.Ordinal))
        "Private paths stay redacted."

    Expect.isFalse (safe.Contains(custodianId, StringComparison.Ordinal)) "Custody stays redacted."

let private verifyPrivateInputRefusals directory custodianId attestationPath signaturePath inputs =
    let rejected = assertRefused directory custodianId signaturePath inputs
    rejected (Path.Combine(directory, "missing-copy-attestation.json"))

    let malformedPath =
        privateBytes directory "malformed-copy-attestation.json" [| 0uy |]

    rejected malformedPath
    let linkedPath = Path.Combine(directory, "linked-copy-attestation.json")
    File.CreateSymbolicLink(linkedPath, attestationPath) |> ignore
    rejected linkedPath

let private assertExactRetry (witness: WitnessProtocol) attestationPath signaturePath inputs =
    let beforeRetry =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let retryCode, retry =
        runCommand "verify-delete-managed-copy" [ attestationPath; signaturePath ] inputs

    use retry = retry
    Expect.equal retryCode 0 "Exact owner retry remains confirmed."

    Expect.equal
        (retry.RootElement.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "Exact retry reuses the one witnessed deletion event."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        beforeRetry
        "Exact retry creates no second witness event or approval use."

let private complete
    owner
    writer
    directory
    (witness: WitnessProtocol)
    registryPath
    reportPath
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    (pending: PendingSyntheticCopy)
    (approval: CopyDeletionApprovalRequest)
    tip
    observedAt
    =
    let canonical = transition pending approval tip observedAt
    let signature = algorithm.Sign(copyKey, canonical)

    let attestationPath = privateBytes directory "verified-deletion.json" canonical
    let signaturePath = privateBytes directory "verified-deletion.sig" signature

    let inputs =
        DatabaseVerifyDataTests.files directory owner writer witness
        @ [
            "CLAIMCORE_COPY_LOCATION_REGISTRY_FILE", registryPath
            "CLAIMCORE_COPY_LOCATION_INSPECTION_FILE", reportPath
            "CLAIMCORE_COPY_COMMITMENT_KEY_FILE", pending.KeyPath
        ]

    verifyPrivateInputRefusals directory pending.CustodianId attestationPath signaturePath inputs

    let code, result =
        runCommand "verify-delete-managed-copy" [ attestationPath; signaturePath ] inputs

    use result = result
    Expect.equal code 0 "Owner process confirmed exact verified deletion."

    Expect.equal
        (result.RootElement.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "Owner process reports a definite co-commit."

    Expect.equal
        (result.RootElement.GetProperty("command").GetString())
        "VERIFY_DELETE_MANAGED_COPY"
        "Owner process reports the exact command token."

    assertExactRetry witness attestationPath signaturePath inputs

let finishPending
    owner
    writer
    (witness: WitnessProtocol)
    (runtime: Runtime)
    directory
    registryKey
    verifierKey
    (copyKey: Key)
    algorithm
    registryKeyId
    verifierKeyId
    verifierPrincipal
    (pending: PendingSyntheticCopy)
    =
    File.Delete(pending.CopyPath)

    let tip = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    let registryPath, reportPath, observedAt, approval =
        prepareEvidence
            runtime
            verifierPrincipal
            directory
            tip
            registryKeyId
            verifierKeyId
            pending.CopyId
            pending.CustodianId
            pending.CopyPath
            pending.Ciphertext
            registryKey
            verifierKey
            algorithm
            pending.Registration

    complete
        owner
        writer
        directory
        witness
        registryPath
        reportPath
        copyKey
        algorithm
        pending
        approval
        tip
        observedAt

    pending.CopyId
