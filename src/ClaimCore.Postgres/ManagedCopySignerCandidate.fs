namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.Application

/// Exact, bounded authority candidates; neither includes a public-key trust claim from a copy.
module internal ManagedCopySignerCandidate =
    let actionName =
        function
        | CopySignerAction.Register -> "REGISTER"
        | CopySignerAction.Retire -> "RETIRE"

    let approvalRole =
        function
        | CopySignerApprovalRole.Owner _ -> "OWNER"
        | CopySignerApprovalRole.Custodian -> "CUSTODIAN"

    let purposeName =
        function
        | CopySignerPurpose.CopyAttestor -> "COPY_ATTESTOR"
        | CopySignerPurpose.LocationRegistry -> "LOCATION_REGISTRY"
        | CopySignerPurpose.LocationInspector -> "LOCATION_INSPECTOR"
        | CopySignerPurpose.DeletionVerifier -> "DELETION_VERIFIER"
        | CopySignerPurpose.RestoreReport -> "RESTORE_REPORT"
        | CopySignerPurpose.Checkpoint -> "CHECKPOINT"
        | CopySignerPurpose.WriterHandoffAbort -> "WRITER_HANDOFF_ABORT"
        | CopySignerPurpose.RestoreCopyVerifier -> "RESTORE_COPY_VERIFIER"
        | CopySignerPurpose.InstallationLossRetirement -> "INSTALLATION_LOSS_RETIREMENT"

    let purposeOfName =
        function
        | "COPY_ATTESTOR" -> CopySignerPurpose.CopyAttestor
        | "LOCATION_REGISTRY" -> CopySignerPurpose.LocationRegistry
        | "LOCATION_INSPECTOR" -> CopySignerPurpose.LocationInspector
        | "DELETION_VERIFIER" -> CopySignerPurpose.DeletionVerifier
        | "RESTORE_REPORT" -> CopySignerPurpose.RestoreReport
        | "CHECKPOINT" -> CopySignerPurpose.Checkpoint
        | "WRITER_HANDOFF_ABORT" -> CopySignerPurpose.WriterHandoffAbort
        | "RESTORE_COPY_VERIFIER" -> CopySignerPurpose.RestoreCopyVerifier
        | "INSTALLATION_LOSS_RETIREMENT" -> CopySignerPurpose.InstallationLossRetirement
        | _ -> invalidOp "Signer purpose is invalid."

    let private writeBytes write =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer
        writer.Flush()
        stream.ToArray()

    let approval
        (request: CopySignerApprovalRequest)
        (actorId: Guid)
        (actorRole: string)
        (grantRevision: int64)
        =
        writeBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("approvalId", request.ApprovalId)
            writer.WriteString("signingKeyId", request.SigningKeyId)
            writer.WriteString("action", actionName request.Action)
            writer.WriteString("purpose", purposeName request.Purpose)

            match request.Role with
            | CopySignerApprovalRole.Owner holderId ->
                writer.WriteString("holderApprovalId", holderId)
            | CopySignerApprovalRole.Custodian -> writer.WriteNull("holderApprovalId")

            writer.WriteString(
                "publicKeySha256",
                Convert.ToHexStringLower(request.PublicKeySha256)
            )

            writer.WriteString("actorId", actorId)
            writer.WriteString("actorRole", actorRole)
            writer.WriteNumber("grantRevision", grantRevision)

            writer.WriteString(
                "expiresAt",
                request.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            )

            writer.WriteEndObject())

    let roster
        (eventId: Guid)
        (signingKeyId: Guid)
        (action: string)
        (purpose: CopySignerPurpose)
        (revision: int64)
        (publicKeySha256: byte array)
        (ownerActorId: Guid)
        (custodianActorId: Guid)
        (ownerApprovalId: Guid)
        (custodianApprovalId: Guid)
        (ownerCandidate: byte array)
        (custodianCandidate: byte array)
        =
        writeBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("eventId", eventId)
            writer.WriteString("signingKeyId", signingKeyId)
            writer.WriteString("action", action)
            writer.WriteString("purpose", purposeName purpose)
            writer.WriteString("holderActorId", custodianActorId)
            writer.WriteNumber("revision", revision)
            writer.WriteString("publicKeySha256", Convert.ToHexStringLower(publicKeySha256))
            writer.WriteString("ownerActorId", ownerActorId)
            writer.WriteString("custodianActorId", custodianActorId)
            writer.WriteString("ownerApprovalId", ownerApprovalId)
            writer.WriteString("custodianApprovalId", custodianApprovalId)
            writer.WriteString("ownerApprovalSha256", Convert.ToHexStringLower(ownerCandidate))

            writer.WriteString(
                "custodianApprovalSha256",
                Convert.ToHexStringLower(custodianCandidate)
            )

            writer.WriteEndObject())

    let eventHash (previousHash: byte array) (canonical: byte array) =
        if previousHash.Length <> 32 || canonical.Length = 0 || canonical.Length > 8192 then
            invalidArg (nameof canonical) "Signer event evidence is invalid."

        let length = Array.zeroCreate<byte> 4
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        hash.AppendData(Encoding.ASCII.GetBytes("CLAIMCORE_MANAGED_COPY_SIGNER_EVENT_V1\000"))
        hash.AppendData(previousHash)
        BinaryPrimitives.WriteInt32BigEndian(length, canonical.Length)
        hash.AppendData(length)
        hash.AppendData(canonical)
        hash.GetHashAndReset()
