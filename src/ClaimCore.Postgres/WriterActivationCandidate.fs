namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// A deterministic authority identity and bounded, nonpayload activation candidate.
module internal WriterActivationCandidate =
    let private domain = Encoding.ASCII.GetBytes("claimcore-writer-activation-v1:")

    let activationId (handoffId: Guid) =
        if handoffId = Guid.Empty then
            invalidArg (nameof handoffId) "Writer handoff identity is required."

        let source = Array.append domain (Encoding.ASCII.GetBytes(handoffId.ToString("D")))
        let digest = SHA256.HashData(source)
        let bytes = digest.AsSpan(0, 16).ToArray()
        bytes[6] <- (bytes[6] &&& 0x0fuy) ||| 0x80uy
        bytes[8] <- (bytes[8] &&& 0x3fuy) ||| 0x80uy
        Guid(bytes)

    let private hex (bytes: byte array) =
        if isNull (box bytes) || bytes.Length <> 32 then
            invalidArg (nameof bytes) "Writer activation digest is invalid."

        Convert.ToHexStringLower(bytes)

    let private signedDigest (bytes: byte array) (signature: byte array) =
        if
            isNull (box bytes)
            || bytes.Length = 0
            || isNull (box signature)
            || signature.Length <> 64
        then
            invalidArg (nameof bytes) "Writer activation signature evidence is invalid."

        hex (SHA256.HashData(signature))

    let encode (value: WriterActivationEvidence) =
        let id = activationId value.HandoffId
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("kind", "WRITER_ACTIVATION")
        writer.WriteString("activationId", id)
        writer.WriteString("handoffId", value.HandoffId)
        writer.WriteString("installationId", value.InstallationId)
        writer.WriteString("lineageId", value.LineageId)
        writer.WriteNumber("epoch", value.Epoch)
        writer.WriteNumber("writerGeneration", value.WriterGeneration)
        writer.WriteNumber("w1Sequence", value.W1Sequence)
        writer.WriteString("w1Hash", hex value.W1Hash)
        writer.WriteString("publicationManifestSha256", hex value.PublicationManifestSha256)
        writer.WriteString("reportSha256", hex value.ReportSha256)
        writer.WriteString("fenceSha256", hex value.FenceSha256)
        writer.WriteString("supplementSha256", hex value.SupplementSha256)
        writer.WriteString("finalWalObjectSha256", hex value.FinalWalObjectSha256)
        writer.WriteNumber("finalWalObjectCount", value.FinalWalObjectCount)
        writer.WriteString("independentProbeSha256", hex value.IndependentProbeSha256)
        writer.WriteString("probeEvidenceSha256", hex value.ProbeEvidenceSha256)
        writer.WriteString("checkpointSigningKeyId", value.CheckpointSigningKeyId)
        writer.WriteString("checkpointHolderActorId", value.CheckpointHolderActorId)

        writer.WriteString(
            "reportSignatureSha256",
            signedDigest value.SignedReport value.ReportSignature
        )

        writer.WriteString(
            "fenceSignatureSha256",
            signedDigest value.SignedFence value.FenceSignature
        )

        writer.WriteString(
            "supplementSignatureSha256",
            signedDigest value.SignedSupplement value.SupplementSignature
        )

        writer.WriteString("validUntil", value.ValidUntil.ToUniversalTime().ToString("O"))
        writer.WriteEndObject()
        writer.Flush()
        let canonical = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        canonical
