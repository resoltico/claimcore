module internal ClaimCore.IntegrationTests.WriterHandoffDocuments

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.Json
open ClaimCore.Witness

let private canonical pairs =
    let fields = SortedDictionary<string, objnull>(StringComparer.Ordinal)

    for name, value in pairs do
        fields.Add(name, value)

    JsonSerializer.Serialize(fields) + "\n" |> Encoding.ASCII.GetBytes

let private hex (bytes: byte array) = Convert.ToHexStringLower(bytes)
let private uuid (value: Guid) = value.ToString("D")

let private stamp (value: DateTimeOffset) =
    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)

let prepare
    (identity: Identity)
    handoffId
    oldGeneration
    reviewedSequence
    reviewedHash
    expectedSequence
    expectedHash
    newCapabilitySha256
    signerKeyId
    fenceSha256
    inventorySha256
    reportSha256
    approvalOne
    approvalTwo
    validUntil
    =
    canonical
        [
            "format", box "claimcore-writer-handoff-1"
            "stage", box "PREPARE"
            "handoffId", box (uuid handoffId)
            "installationId", box (uuid identity.InstallationId)
            "lineageId", box (uuid identity.LineageId)
            "epoch", box identity.Epoch
            "oldGeneration", box oldGeneration
            "newGeneration", box (oldGeneration + 1L)
            "reviewedCutoffSequence", box reviewedSequence
            "reviewedCutoffHash", box (hex reviewedHash)
            "expectedTipSequence", box expectedSequence
            "expectedTipHash", box (hex expectedHash)
            "newCapabilitySha256", box (hex newCapabilitySha256)
            "checkpointSigningKeyId", box (uuid signerKeyId)
            "fenceReportSha256", box (hex fenceSha256)
            "inventorySha256", box (hex inventorySha256)
            "restoreReportSha256", box (hex reportSha256)
            "approvalOneId", box (uuid approvalOne)
            "approvalTwoId", box (uuid approvalTwo)
            "validUntil", box (stamp validUntil)
        ]

let settlement
    (identity: Identity)
    handoffId
    oldGeneration
    prepareSequence
    prepareHash
    prepareCanonicalSha256
    newCapabilitySha256
    signerKeyId
    fenceSha256
    inventorySha256
    reportSha256
    approvalOne
    approvalTwo
    validUntil
    =
    canonical
        [
            "format", box "claimcore-writer-handoff-1"
            "stage", box "COMMIT"
            "handoffId", box (uuid handoffId)
            "installationId", box (uuid identity.InstallationId)
            "lineageId", box (uuid identity.LineageId)
            "epoch", box identity.Epoch
            "oldGeneration", box oldGeneration
            "newGeneration", box (oldGeneration + 1L)
            "prepareSequence", box prepareSequence
            "prepareHash", box (hex prepareHash)
            "prepareCanonicalSha256", box (hex prepareCanonicalSha256)
            "newCapabilitySha256", box (hex newCapabilitySha256)
            "checkpointSigningKeyId", box (uuid signerKeyId)
            "fenceReportSha256", box (hex fenceSha256)
            "inventorySha256", box (hex inventorySha256)
            "restoreReportSha256", box (hex reportSha256)
            "approvalOneId", box (uuid approvalOne)
            "approvalTwoId", box (uuid approvalTwo)
            "validUntil", box (stamp validUntil)
        ]

let abort
    (identity: Identity)
    handoffId
    oldGeneration
    prepareSequence
    prepareHash
    prepareCanonicalSha256
    oldCapabilitySha256
    newCapabilitySha256
    signingKeyOne
    signingKeyTwo
    approvalOne
    approvalTwo
    ownerOne
    ownerTwo
    grantOne
    grantTwo
    authorityRevision
    validUntil
    =
    canonical
        [
            "format", box "claimcore-writer-handoff-1"
            "stage", box "ABORT"
            "handoffId", box (uuid handoffId)
            "installationId", box (uuid identity.InstallationId)
            "lineageId", box (uuid identity.LineageId)
            "epoch", box identity.Epoch
            "oldGeneration", box oldGeneration
            "prepareSequence", box prepareSequence
            "prepareHash", box (hex prepareHash)
            "prepareCanonicalSha256", box (hex prepareCanonicalSha256)
            "oldCapabilitySha256", box (hex oldCapabilitySha256)
            "newCapabilitySha256", box (hex newCapabilitySha256)
            "abortSigningKeyOneId", box (uuid signingKeyOne)
            "abortSigningKeyTwoId", box (uuid signingKeyTwo)
            "approvalOneId", box (uuid approvalOne)
            "approvalTwoId", box (uuid approvalTwo)
            "ownerOneActorId", box (uuid ownerOne)
            "ownerTwoActorId", box (uuid ownerTwo)
            "ownerOneGrantRevision", box grantOne
            "ownerTwoGrantRevision", box grantTwo
            "expectedAuthorityRevision", box authorityRevision
            "validUntil", box (stamp validUntil)
        ]
