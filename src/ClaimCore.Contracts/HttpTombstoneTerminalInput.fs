namespace ClaimCore.Contracts

open System
open System.Text.Json
open ClaimCore.Application
open HttpInputSupport

type HttpTombstoneTerminalApprovalInput =
    {
        Proposal: TombstoneTerminalProposal
        ApprovalId: Guid
        ExpiresAt: DateTimeOffset
    }

module HttpTombstoneTerminalInput =
    let private uuid value =
        value |> stringValue |> operationIdValue

    let private revision value =
        value |> stringValue |> canonicalNonNegativeInt64

    let private digest value = value |> stringValue |> digestValue

    let private instant value =
        value |> stringValue |> utcTimestampValue

    let private policy value =
        let text = stringValue value

        if
            String.IsNullOrWhiteSpace(text)
            || text.Length > 128
            || text <> text.Trim()
            || (text |> Seq.exists Char.IsControl)
        then
            fail HttpInputProblem.InvalidJson

        text

    let private copy (element: JsonElement) : TerminalCopyProposal =
        let values =
            properties element
            |> exactProperties
                [
                    "eventId"
                    "caseId"
                    "expectedAuthorityRevision"
                    "expectedAuthorityHash"
                    "installationId"
                    "lineageId"
                    "witnessEpoch"
                    "pruneEventId"
                    "witnessCutoffSequence"
                    "witnessCutoffHash"
                    "copyInventoryDigest"
                    "relevantCopyCount"
                    "expectedWriterGeneration"
                    "policyId"
                    "suppressionUntil"
                    "validUntil"
                ]

        {
            EventId = required "eventId" values |> uuid
            CaseId = required "caseId" values |> uuid
            ExpectedAuthorityRevision = required "expectedAuthorityRevision" values |> revision
            ExpectedAuthorityHash = required "expectedAuthorityHash" values |> digest
            InstallationId = required "installationId" values |> uuid
            LineageId = required "lineageId" values |> uuid
            WitnessEpoch = required "witnessEpoch" values |> revision
            PruneEventId = required "pruneEventId" values |> uuid
            WitnessCutoffSequence = required "witnessCutoffSequence" values |> revision
            WitnessCutoffHash = required "witnessCutoffHash" values |> digest
            CopyInventoryDigest = required "copyInventoryDigest" values |> digest
            RelevantCopyCount = required "relevantCopyCount" values |> revision
            ExpectedWriterGeneration = required "expectedWriterGeneration" values |> revision
            PolicyId = required "policyId" values |> policy
            SuppressionUntil = required "suppressionUntil" values |> instant
            ValidUntil = required "validUntil" values |> instant
        }

    let private proposal (element: JsonElement) =
        let values = properties element

        match required "kind" values |> stringValue with
        | "CONFIRM_MANAGED_PAYLOAD_ABSENCE" ->
            exactProperties [ "kind"; "copy" ] values |> ignore

            required "copy" values
            |> copy
            |> TombstoneTerminalProposal.ConfirmManagedPayloadAbsence
        | "COMPLETE_SUPPRESSION_HORIZON" ->
            exactProperties
                [
                    "kind"
                    "copy"
                    "recoveryFenceDigest"
                    "oldWriterGeneration"
                    "newWriterGeneration"
                ]
                values
            |> ignore

            TombstoneTerminalProposal.CompleteSuppressionHorizon
                {
                    Copy = required "copy" values |> copy
                    RecoveryFenceDigest = required "recoveryFenceDigest" values |> digest
                    OldWriterGeneration = required "oldWriterGeneration" values |> revision
                    NewWriterGeneration = required "newWriterGeneration" values |> revision
                }
        | _ -> fail HttpInputProblem.InvalidJson

    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root |> exactProperties [ "proposal"; "approvalId"; "expiresAt" ]

            {
                Proposal = required "proposal" values |> proposal
                ApprovalId = required "approvalId" values |> uuid
                ExpiresAt = required "expiresAt" values |> instant
            })
