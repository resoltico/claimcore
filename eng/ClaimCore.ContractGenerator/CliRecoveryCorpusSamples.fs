namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliRecoveryCorpusSamples =
    let private sample = CliCorpusValues.sample

    let export =
        let endpoint = "recovery.export"
        let encode = CliWireCodec.recoveryExport endpoint CliCorpusValues.operationId

        [
            sample
                "recovery-export-exported"
                endpoint
                (CliWireCodec.exported
                    endpoint
                    CliCorpusValues.operationId
                    CliCorpusValues.digest
                    CliCorpusValues.recoveryExport.MediaType)
            sample
                "recovery-export-identity-conflict"
                endpoint
                (CliWireCodec.exportIdentityConflict endpoint)
            sample
                "recovery-export-not-found"
                endpoint
                (encode (RecoverySucceeded(NotFound CliCorpusValues.operationId)))
            sample
                "recovery-export-rejected"
                endpoint
                (encode (RecoveryRejected CliCorpusValues.recoveryRejection))
            sample "recovery-export-failed" endpoint (encode (RecoveryFailed CliCorpusValues.fault))
            sample "recovery-export-cancelled" endpoint (encode RecoveryCancelled)
        ]

    let private preview endpoint =
        let encode = CliWireCodec.importPreview endpoint

        [
            sample
                (endpoint + "-previewed")
                endpoint
                (encode (RecoverySucceeded CliCorpusValues.importPreview))
            sample
                (endpoint + "-rejected")
                endpoint
                (encode (RecoveryRejected CliCorpusValues.recoveryRejection))
            sample (endpoint + "-failed") endpoint (encode (RecoveryFailed CliCorpusValues.fault))
            sample (endpoint + "-cancelled") endpoint (encode RecoveryCancelled)
        ]

    let private retain endpoint =
        let encode = CliWireCodec.importRetain endpoint

        [
            sample
                (endpoint + "-retained")
                endpoint
                (encode (RetainedPreparation CliCorpusValues.preparationDetails))
            sample
                (endpoint + "-existing")
                endpoint
                (encode (ExistingPreparation CliCorpusValues.preparationDetails))
            sample
                (endpoint + "-observed-accepted")
                endpoint
                (encode (ObservedAcceptedImport CliCorpusValues.observedReceipt))
            sample
                (endpoint + "-rejected")
                endpoint
                (encode (ImportRejected CliCorpusValues.recoveryRejection))
            sample (endpoint + "-failed") endpoint (encode (ImportFailed CliCorpusValues.fault))
            sample (endpoint + "-cancelled") endpoint (encode ImportCancelledBeforeAdmission)
            sample
                (endpoint + "-state-unknown-with-operation")
                endpoint
                (encode (
                    RetainStateUnknown(
                        RecoveryArtifactKind.Envelope,
                        CliCorpusValues.digest,
                        Some CliCorpusValues.operationId,
                        CliCorpusValues.fault
                    )
                ))
            sample
                (endpoint + "-state-unknown-without-operation")
                endpoint
                (encode (
                    RetainStateUnknown(
                        RecoveryArtifactKind.UnboundCanonicalRecord,
                        CliCorpusValues.digest,
                        None,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let all =
        export
        @ preview "recovery.importEnvelopePreview"
        @ retain "recovery.importEnvelopeRetain"
        @ preview "recovery.importRecordPreview"
        @ retain "recovery.importRecordRetain"
