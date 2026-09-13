namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal WebImportRetainCorpusSamples =
    let private sample = WebCorpusSamples.sample

    let private retainDefinite endpoint =
        let encode = WebWireCodec.importRetain endpoint

        [
            sample
                (endpoint + "-retained")
                endpoint
                (encode (
                    RecoveryImportRetainOutcome.RetainedPreparation
                        CliCorpusValues.preparationDetails
                ))
            sample
                (endpoint + "-existing")
                endpoint
                (encode (
                    RecoveryImportRetainOutcome.ExistingPreparation
                        WebCorpusSamples.detailsWithNullableValues
                ))
            sample
                (endpoint + "-rejected")
                endpoint
                (encode (
                    RecoveryImportRetainOutcome.ImportRejected CliCorpusValues.recoveryRejection
                ))
            sample
                (endpoint + "-failed")
                endpoint
                (encode (RecoveryImportRetainOutcome.ImportFailed CliCorpusValues.fault))
            sample
                (endpoint + "-cancelled")
                endpoint
                (encode RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission)
        ]

    let private retainUnknown endpoint retainedKind unknownKind =
        let encode = WebWireCodec.importRetain endpoint

        [
            sample
                (endpoint + "-state-unknown-with-operation")
                endpoint
                (encode (
                    RecoveryImportRetainOutcome.RetainStateUnknown(
                        retainedKind,
                        CliCorpusValues.digest,
                        Some CliCorpusValues.operationId,
                        CliCorpusValues.fault
                    )
                ))
            sample
                (endpoint + "-state-unknown-without-operation")
                endpoint
                (encode (
                    RecoveryImportRetainOutcome.RetainStateUnknown(
                        unknownKind,
                        CliCorpusValues.digest,
                        None,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let private retain endpoint retainedKind unknownKind =
        retainDefinite endpoint @ retainUnknown endpoint retainedKind unknownKind

    let all =
        retain
            "recovery.importEnvelopeRetain"
            RecoveryArtifactKind.Envelope
            RecoveryArtifactKind.UnboundCanonicalRecord
        @ retain
            "recovery.importRecordRetain"
            RecoveryArtifactKind.UnboundCanonicalRecord
            RecoveryArtifactKind.Envelope
