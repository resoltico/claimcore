namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain
open HttpInputSupport

type HttpTombstoneApprovalInput =
    {
        Proposal: TombstonePruneProposal
        ApprovalId: Guid
        ExpiresAt: DateTimeOffset
    }

module HttpTombstoneInput =
    let private uuid (element: JsonElement) =
        element |> stringValue |> operationIdValue

    let private sequence (element: JsonElement) =
        element |> stringValue |> canonicalNonNegativeInt64

    let private digest (element: JsonElement) = element |> stringValue |> digestValue

    let private date (element: JsonElement) =
        let value = element |> stringValue
        let mutable parsed = DateOnly.MinValue

        if
            not (
                DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    &parsed
                )
            )
        then
            fail HttpInputProblem.InvalidJson

        parsed

    let private proposal (element: JsonElement) =
        let values =
            properties element
            |> exactProperties
                [
                    "eventId"
                    "caseId"
                    "purgeEventId"
                    "purgeWitnessSequence"
                    "purgeWitnessEpoch"
                    "purgeWitnessHash"
                    "cutoffSequence"
                    "cutoffHash"
                    "targetCount"
                    "targetDigest"
                    "expectedAuthorityRevision"
                    "expectedAuthorityHash"
                    "validUntil"
                ]

        {
            EventId = required "eventId" values |> uuid
            CaseId = required "caseId" values |> uuid
            PurgeEventId = required "purgeEventId" values |> uuid
            PurgeWitnessSequence = required "purgeWitnessSequence" values |> sequence
            PurgeWitnessEpoch = required "purgeWitnessEpoch" values |> sequence
            PurgeWitnessHash = required "purgeWitnessHash" values |> digest
            CutoffSequence = required "cutoffSequence" values |> sequence
            CutoffHash = required "cutoffHash" values |> digest
            TargetCount = required "targetCount" values |> sequence
            TargetDigest = required "targetDigest" values |> digest
            ExpectedAuthorityRevision = required "expectedAuthorityRevision" values |> sequence
            ExpectedAuthorityHash = required "expectedAuthorityHash" values |> digest
            ValidUntil = required "validUntil" values |> stringValue |> utcTimestampValue
        }

    let private closedCode allowed element =
        let token = element |> stringValue

        if not (List.contains token allowed) then
            fail HttpInputProblem.InvalidJson

        token

    let private mutation (element: JsonElement) =
        let values = properties element

        match required "kind" values |> stringValue with
        | "RECORD" ->
            exactProperties [ "kind"; "holdId"; "groundCode"; "reviewOn" ] values |> ignore

            TombstoneHoldMutation.Record(
                required "holdId" values |> uuid,
                required "groundCode" values |> closedCode TombstoneHoldPolicy.groundCodes,
                required "reviewOn" values |> date
            )
        | "RELEASE" ->
            exactProperties [ "kind"; "holdId"; "releaseCode" ] values |> ignore

            TombstoneHoldMutation.Release(
                required "holdId" values |> uuid,
                required "releaseCode" values |> closedCode TombstoneHoldPolicy.releaseCodes
            )
        | _ -> fail HttpInputProblem.InvalidJson

    let review bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "caseId" ]
            required "caseId" values |> uuid)

    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root |> exactProperties [ "proposal"; "approvalId"; "expiresAt" ]

            {
                Proposal = required "proposal" values |> proposal
                ApprovalId = required "approvalId" values |> uuid
                ExpiresAt = required "expiresAt" values |> stringValue |> utcTimestampValue
            })

    let hold bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "eventId"
                        "caseId"
                        "expectedAuthorityRevision"
                        "expectedAuthorityHash"
                        "mutation"
                    ]

            {
                EventId = required "eventId" values |> uuid
                CaseId = required "caseId" values |> uuid
                ExpectedAuthorityRevision = required "expectedAuthorityRevision" values |> sequence
                ExpectedAuthorityHash = required "expectedAuthorityHash" values |> digest
                Mutation = required "mutation" values |> mutation
            })
