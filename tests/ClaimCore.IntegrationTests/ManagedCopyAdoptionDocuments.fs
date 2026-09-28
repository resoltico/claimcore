module internal ClaimCore.IntegrationTests.ManagedCopyAdoptionDocuments

open System
open System.Security.Cryptography
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

let private hex (value: byte array) = Convert.ToHexStringLower value

[<NoEquality; NoComparison>]
type internal AdoptionSource =
    {
        CopyId: Guid
        Sha256: byte array
        Bytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        Sequence: int64
        EntryHash: byte array
        PreviousHash: byte array
        EncryptionKeyId: Guid
    }

let private originFields (value: AdoptionSource) origin =
    match origin with
    | CopyAdoptionOrigin.ProductExport _ ->
        [
            "originKind", element "PRODUCT_EXPORT"
            "exportId", element (value.CopyId.ToString("D"))
            "preFenceSequence", element value.Sequence
            "preFenceHash", element (hex value.EntryHash)
        ]
    | CopyAdoptionOrigin.AdoptedExternal _ ->
        [
            "originKind", element "ADOPTED_EXTERNAL"
            "exportId", nil
            "preFenceSequence", element value.Sequence
            "preFenceHash", element (hex value.EntryHash)
        ]

let private copyStateFields origin =
    match origin with
    | CopyAdoptionOrigin.ProductExport _ ->
        [
            "eventKind", element "ADOPT"
            "producerKind", element "PRODUCT_EXPORT"
            "state", element "UNVERIFIED"
            "copyRevision", element 2L
        ]
    | CopyAdoptionOrigin.AdoptedExternal _ ->
        [
            "eventKind", element "REGISTER"
            "producerKind", element "ADOPTED_EXTERNAL"
            "state", element "UNKNOWN"
            "copyRevision", element 1L
        ]

let private custodyFields
    (witness: WitnessProtocol)
    (source: AdoptionSource)
    origin
    (eventId: Guid)
    (approvalId: Guid)
    (caseId: Guid)
    (location: byte array)
    (custodian: byte array)
    (keyId: Guid)
    (validUntil: DateTimeOffset)
    =
    [
        "format", element "claimcore-copy-adoption-custody-1"
        "adoptionEventId", element (eventId.ToString("D"))
        "copyId", element (source.CopyId.ToString("D"))
        "caseId", element (caseId.ToString("D"))
        "previousEventHash", element (hex source.PreviousHash)
        "ciphertextSha256", element (hex source.Sha256)
        "ciphertextBytes", element source.Bytes
        "capturedAt", element (source.CapturedAt.ToString("O"))
        "retainUntil", element (source.RetainUntil.ToString("O"))
        "locationCommitment", element (hex location)
        "custodianCommitment", element (hex custodian)
        "signingKeyId", element (keyId.ToString("D"))
        "encryptionKeyId", element (source.EncryptionKeyId.ToString("D"))
        "ownerApprovalId", element (approvalId.ToString("D"))
        "installationId", element (witness.Identity.InstallationId.ToString("D"))
        "lineageId", element (witness.Identity.LineageId.ToString("D"))
        "epoch", element witness.Identity.Epoch
        "validUntil", element (validUntil.ToString("O"))
    ]
    @ copyStateFields origin
    @ originFields source origin

let private registryFields
    (source: AdoptionSource)
    (eventId: Guid)
    (caseId: Guid)
    (location: byte array)
    (custodian: byte array)
    (custodyHash: byte array)
    (keyId: Guid)
    (now: DateTimeOffset)
    (until: DateTimeOffset)
    =
    [
        "format", element "claimcore-copy-adoption-registry-1"
        "adoptionEventId", element (eventId.ToString("D"))
        "copyId", element (source.CopyId.ToString("D"))
        "caseId", element (caseId.ToString("D"))
        "locationCommitment", element (hex location)
        "custodianCommitment", element (hex custodian)
        "custodianCanonicalSha256", element (hex custodyHash)
        "signingKeyId", element (keyId.ToString("D"))
        "observedAt", element (now.ToString("O"))
        "validUntil", element (until.ToString("O"))
    ]

let private inspectionFields
    (source: AdoptionSource)
    (eventId: Guid)
    (caseId: Guid)
    (location: byte array)
    (registryHash: byte array)
    (keyId: Guid)
    (now: DateTimeOffset)
    (until: DateTimeOffset)
    =
    [
        "format", element "claimcore-copy-adoption-inspection-1"
        "adoptionEventId", element (eventId.ToString("D"))
        "copyId", element (source.CopyId.ToString("D"))
        "caseId", element (caseId.ToString("D"))
        "status", element "PRESENT"
        "locationCommitment", element (hex location)
        "registryCanonicalSha256", element (hex registryHash)
        "ciphertextSha256", element (hex source.Sha256)
        "ciphertextBytes", element source.Bytes
        "signingKeyId", element (keyId.ToString("D"))
        "observedAt", element (now.ToString("O"))
        "validUntil", element (until.ToString("O"))
    ]

let private registryBody
    (source: AdoptionSource)
    (eventId: Guid)
    (caseId: Guid)
    (location: byte array)
    (custodian: byte array)
    (custody: byte array)
    (registryId: Guid)
    (now: DateTimeOffset)
    =
    registryFields
        source
        eventId
        caseId
        location
        custodian
        (SHA256.HashData custody)
        registryId
        now
        (now.AddMinutes(5.0))
    |> canonical

let private inspectionBody
    (source: AdoptionSource)
    (eventId: Guid)
    (caseId: Guid)
    (location: byte array)
    (registry: byte array)
    (inspectorId: Guid)
    (now: DateTimeOffset)
    =
    inspectionFields
        source
        eventId
        caseId
        location
        (SHA256.HashData registry)
        inspectorId
        now
        (now.AddMinutes(5.0))
    |> canonical

let private bodies
    witness
    source
    origin
    caseId
    eventId
    approvalId
    location
    custodian
    (copyId: Guid)
    (registryId: Guid)
    (inspectorId: Guid)
    (now: DateTimeOffset)
    validUntil
    =
    let custody =
        custodyFields
            witness
            source
            origin
            eventId
            approvalId
            caseId
            location
            custodian
            copyId
            validUntil
        |> canonical

    let registry =
        registryBody source eventId caseId location custodian custody registryId now

    let inspection =
        inspectionBody source eventId caseId location registry inspectorId now

    custody, registry, inspection

let signedDocsForOrigin
    (witness: WitnessProtocol)
    (source: AdoptionSource)
    origin
    (caseId: Guid)
    (eventId: Guid)
    (approvalId: Guid)
    (location: byte array)
    (custodian: byte array)
    (keys: Key * Key * Key * SignatureAlgorithm * Guid * Guid * Guid)
    (now: DateTimeOffset)
    (validUntil: DateTimeOffset)
    =
    let copyKey, registryKey, inspectorKey, algorithm, copyId, registryId, inspectorId =
        keys

    let custody, registry, inspection =
        bodies
            witness
            source
            origin
            caseId
            eventId
            approvalId
            location
            custodian
            copyId
            registryId
            inspectorId
            now
            validUntil

    {
        ApprovalId = approvalId
        AdoptionEventId = eventId
        Custodian =
            {
                Canonical = custody
                Signature = algorithm.Sign(copyKey, custody)
            }
        Registry =
            {
                Canonical = registry
                Signature = algorithm.Sign(registryKey, registry)
            }
        Inspection =
            {
                Canonical = inspection
                Signature = algorithm.Sign(inspectorKey, inspection)
            }
    },
    (copyId, registryId, inspectorId)

let signedDocs
    witness
    (source: AdoptionSource)
    caseId
    eventId
    approvalId
    location
    custodian
    keys
    now
    validUntil
    =
    signedDocsForOrigin
        witness
        source
        (CopyAdoptionOrigin.ProductExport(source.CopyId, source.Sequence, source.EntryHash))
        caseId
        eventId
        approvalId
        location
        custodian
        keys
        now
        validUntil
