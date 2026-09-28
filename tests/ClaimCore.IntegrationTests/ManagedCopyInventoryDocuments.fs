module internal ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments

open System
open System.Text.Json
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

let baseEntry (copyId: Guid) custodianId copyPath (ciphertext: byte array) =
    canonical
        [
            "copyId", element (copyId.ToString("D"))
            "producerKind", element "OWNER_ATTESTED"
            "custodianId", element custodianId
            "location", element copyPath
            "kind", element "BASE"
            "sourceCaseId", nil
            "ciphertextSha256", element (digest ciphertext)
            "ciphertextBytes", element ciphertext.Length
        ]

let productExportEntry (copyId: Guid) (caseId: Guid) ciphertextSha256 ciphertextBytes =
    canonical
        [
            "copyId", element (copyId.ToString("D"))
            "producerKind", element "PRODUCT_EXPORT"
            "custodianId", nil
            "location", nil
            "kind", element "EXPORT"
            "sourceCaseId", element (caseId.ToString("D"))
            "ciphertextSha256", element ciphertextSha256
            "ciphertextBytes", element ciphertextBytes
        ]

let observation (copyId: Guid) (ciphertext: byte array) =
    canonical
        [
            "copyId", element (copyId.ToString("D"))
            "status", element "PRESENT"
            "sha256", element (digest ciphertext)
            "bytes", element ciphertext.Length
        ]

let unknownObservation (copyId: Guid) =
    canonical
        [
            "copyId", element (copyId.ToString("D"))
            "status", element "UNKNOWN"
            "sha256", nil
            "bytes", nil
        ]

let absentObservation (copyId: Guid) =
    canonical
        [
            "copyId", element (copyId.ToString("D"))
            "status", element "ABSENT"
            "sha256", nil
            "bytes", nil
        ]

let private elements (documents: byte array list) =
    documents
    |> List.map (fun bytes ->
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone())
    |> List.toArray

let registryBodyForEntries (tip: Snapshot) (keyId: Guid) entries knownUnmanaged now =
    canonical
        [
            "format", element "claimcore-copy-location-inventory-1"
            "signingKeyId", element (keyId.ToString("D"))
            "installationId", element (tip.Identity.InstallationId.ToString("D"))
            "lineageId", element (tip.Identity.LineageId.ToString("D"))
            "epoch", element tip.Identity.Epoch
            "witnessCutoffSequence", element tip.TipSequence
            "witnessCutoffHash", element (Convert.ToHexStringLower(tip.TipHash))
            "issuedAt", element (stamp now)
            "expiresAt", element (stamp (now.AddMinutes(5.)))
            "entries", element (elements entries)
            "knownUnmanaged",
            element (knownUnmanaged |> List.map (fun (id: Guid) -> id.ToString("D")))
        ]

let registryBody (tip: Snapshot) keyId copyId custodianId copyPath ciphertext now =
    registryBodyForEntries tip keyId [ baseEntry copyId custodianId copyPath ciphertext ] [] now

let inspectionBodyForObservations (tip: Snapshot) (keyId: Guid) registryHash observations now =
    canonical
        [
            "format", element "claimcore-copy-location-inspection-1"
            "signingKeyId", element (keyId.ToString("D"))
            "registrySha256", element registryHash
            "installationId", element (tip.Identity.InstallationId.ToString("D"))
            "lineageId", element (tip.Identity.LineageId.ToString("D"))
            "epoch", element tip.Identity.Epoch
            "witnessCutoffSequence", element tip.TipSequence
            "witnessCutoffHash", element (Convert.ToHexStringLower(tip.TipHash))
            "issuedAt", element (stamp now)
            "expiresAt", element (stamp (now.AddMinutes(5.)))
            "observations", element (elements observations)
        ]

let inspectionBody (tip: Snapshot) keyId registryHash copyId ciphertext now =
    inspectionBodyForObservations tip keyId registryHash [ observation copyId ciphertext ] now
