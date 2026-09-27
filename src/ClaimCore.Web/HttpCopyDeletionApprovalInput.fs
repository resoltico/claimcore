namespace ClaimCore.Web

open System
open ClaimCore.Application
open HttpInputSupport

module internal HttpCopyDeletionApprovalInput =
    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "approvalId"
                        "deletionEventId"
                        "copyId"
                        "verifierSigningKeyId"
                        "expectedCopyRevision"
                        "locationCommitment"
                        "inspectionReportSha256"
                        "witnessCutoffSequence"
                        "witnessCutoffHash"
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
                DeletionEventId = uuid "deletionEventId"
                CopyId = uuid "copyId"
                VerifierSigningKeyId = uuid "verifierSigningKeyId"
                ExpectedCopyRevision = revision "expectedCopyRevision"
                LocationCommitment = digest "locationCommitment"
                InspectionReportSha256 = digest "inspectionReportSha256"
                WitnessCutoffSequence = revision "witnessCutoffSequence"
                WitnessCutoffHash = digest "witnessCutoffHash"
                ExpiresAt = required "expiresAt" values |> stringValue |> utcTimestampValue
            })
