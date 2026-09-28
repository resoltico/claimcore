module internal ClaimCore.IntegrationTests.ManagedCopyExternalPublicationDocuments

open System
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

let registryBody
    (witness: WitnessProtocol)
    (publicationId: Guid)
    (copyId: Guid)
    (caseId: Guid)
    (keyId: Guid)
    (sha: byte array)
    (bytes: int64)
    (captured: DateTimeOffset)
    (retained: DateTimeOffset)
    (location: byte array)
    (custodian: byte array)
    (signer: Guid)
    (issued: DateTimeOffset)
    =
    canonical
        [
            "format", element "claimcore-external-copy-registry-publication-1"
            "publicationId", element (publicationId.ToString("D"))
            "copyId", element (copyId.ToString("D"))
            "caseId", element (caseId.ToString("D"))
            "installationId", element (witness.Identity.InstallationId.ToString("D"))
            "lineageId", element (witness.Identity.LineageId.ToString("D"))
            "epoch", element witness.Identity.Epoch
            "encryptionKeyId", element (keyId.ToString("D"))
            "ciphertextSha256", element (Convert.ToHexStringLower sha)
            "ciphertextBytes", element bytes
            "capturedAt", element (captured.ToString("O"))
            "retainUntil", element (retained.ToString("O"))
            "locationCommitment", element (Convert.ToHexStringLower location)
            "custodianCommitment", element (Convert.ToHexStringLower custodian)
            "signingKeyId", element (signer.ToString("D"))
            "issuedAt", element (issued.ToString("O"))
            "validUntil", element (issued.AddMinutes(5.0).ToString("O"))
        ]

let inspectionBody
    (publicationId: Guid)
    (copyId: Guid)
    (caseId: Guid)
    (sha: byte array)
    (bytes: int64)
    (location: byte array)
    (registryHash: byte array)
    (signer: Guid)
    (issued: DateTimeOffset)
    =
    canonical
        [
            "format", element "claimcore-external-copy-publication-inspection-1"
            "publicationId", element (publicationId.ToString("D"))
            "copyId", element (copyId.ToString("D"))
            "caseId", element (caseId.ToString("D"))
            "status", element "PRESENT"
            "ciphertextSha256", element (Convert.ToHexStringLower sha)
            "ciphertextBytes", element bytes
            "locationCommitment", element (Convert.ToHexStringLower location)
            "registryCanonicalSha256", element (Convert.ToHexStringLower registryHash)
            "signingKeyId", element (signer.ToString("D"))
            "observedAt", element (issued.ToString("O"))
            "validUntil", element (issued.AddMinutes(5.0).ToString("O"))
        ]
