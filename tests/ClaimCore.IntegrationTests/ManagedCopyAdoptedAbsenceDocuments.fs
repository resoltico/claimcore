module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedAbsenceDocuments

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments

let private locationEntry
    (origin: VerifiedCopyAdoptionOrigin)
    (publication: PublicationFixture)
    bytes
    =
    canonical
        [
            "copyId", element (origin.CopyId.ToString("D"))
            "producerKind", element origin.ProducerKind
            "custodianId", element publication.CustodianId
            "location", element publication.CiphertextPath
            "kind", element "EXPORT"
            "sourceCaseId", element (origin.CaseId.ToString("D"))
            "ciphertextSha256", element (digest bytes)
            "ciphertextBytes", element bytes.Length
        ]

let signedAbsence
    directory
    (tip: Snapshot)
    (origin: VerifiedCopyAdoptionOrigin)
    (publication: PublicationFixture)
    registryKeyId
    verifierKeyId
    (registryKey: Key)
    (verifierKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    let bytes = File.ReadAllBytes(publication.CiphertextPath)
    File.Delete(publication.CiphertextPath)
    let entry = locationEntry origin publication bytes
    let now = DateTimeOffset.UtcNow
    let registry = registryBodyForEntries tip registryKeyId [ entry ] [] now

    let report =
        inspectionBodyForObservations
            tip
            verifierKeyId
            (digest registry)
            [ absentObservation origin.CopyId ]
            now

    let registryPath =
        privateBytes
            directory
            "adopted-absence-registry.json"
            (envelope algorithm registryKey registry)

    let reportPath =
        privateBytes
            directory
            "adopted-absence-inspection.json"
            (envelope algorithm verifierKey report)

    let observed =
        DateTimeOffset.Parse(
            stamp now,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal
        )

    registryPath, reportPath, report, observed
