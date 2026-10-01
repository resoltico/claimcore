namespace ClaimCore.Contracts

open System
open ClaimCore.Application
open HttpInputSupport

module HttpWriterHandoffApprovalInput =
    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "approvalId"
                        "handoffId"
                        "oldGeneration"
                        "expectedWitnessSequence"
                        "expectedWitnessHash"
                        "newCapabilitySha256"
                        "checkpointSigningKeyId"
                        "fenceReportSha256"
                        "inventorySha256"
                        "expiresAt"
                    ]

            let uuid key =
                required key values |> stringValue |> operationIdValue

            let revision key =
                required key values |> stringValue |> canonicalNonNegativeInt64

            let digest key =
                required key values |> stringValue |> digestValue |> Convert.FromHexString

            {
                ApprovalId = uuid "approvalId"
                HandoffId = uuid "handoffId"
                OldGeneration = revision "oldGeneration"
                ExpectedWitnessSequence = revision "expectedWitnessSequence"
                ExpectedWitnessHash = digest "expectedWitnessHash"
                NewCapabilitySha256 = digest "newCapabilitySha256"
                CheckpointSigningKeyId = uuid "checkpointSigningKeyId"
                FenceReportSha256 = digest "fenceReportSha256"
                InventorySha256 = digest "inventorySha256"
                ExpiresAt =
                    required "expiresAt" values |> stringValue |> utcMicrosecondTimestampValue
            })
