namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Witness

/// Authenticated metadata for orphaned technical intents; claimant bytes never leave this module.
module internal WitnessTechnicalCandidateRead =
    let private text (root: JsonElement) (name: string) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> raise WitnessPending)

    let private parsePrepare (ticket: Ticket) (root: JsonElement) (plain: byte array) =
        let operationId = root.GetProperty("operationId").GetGuid()
        let caseId = root.GetProperty("caseId").GetGuid()
        let preparer = root.GetProperty("preparerActorId").GetGuid()
        let importerElement = root.GetProperty("importerActorId")

        let importer =
            if importerElement.ValueKind = JsonValueKind.Null then
                None
            else
                Some(importerElement.GetGuid())

        let requestSha = text root "requestSha256"
        let canonicalRequest = root.GetProperty("canonicalRequest").GetBytesFromBase64()

        try
            let again =
                WitnessTechnical.prepareCandidate
                    operationId
                    caseId
                    preparer
                    importer
                    (root.GetProperty("grantRevision").GetInt64())
                    requestSha
                    canonicalRequest
                    (text root "applicationVersion")
                    (text root "contractFingerprint")

            try
                if
                    ticket.OperationId <> WitnessEventIdentity.prepareEventId operationId
                    || plain <> again
                    || Convert.ToHexStringLower(SHA256.HashData(canonicalRequest)) <> requestSha
                then
                    raise WitnessPending

                {
                    Kind = "PREPARE"
                    OperationId = operationId
                    CaseId = caseId
                    WitnessEventId = ticket.OperationId
                    RequestSha256 = requestSha
                    AttemptOrdinal = None
                }
            finally
                CryptographicOperations.ZeroMemory(again)
        finally
            CryptographicOperations.ZeroMemory(canonicalRequest)

    let private parseStart (ticket: Ticket) (root: JsonElement) (plain: byte array) =
        let operationId = root.GetProperty("operationId").GetGuid()
        let attemptId = root.GetProperty("attemptId").GetGuid()
        let ordinal = root.GetProperty("attemptOrdinal").GetInt64()
        let caseId = root.GetProperty("caseId").GetGuid()
        let requestSha = text root "requestSha256"
        let role = text root "actorRole"

        if role <> "SUBMITTER" && role <> "RESOLVER" then
            raise WitnessPending

        let again =
            WitnessTechnical.startCandidate
                operationId
                attemptId
                ordinal
                caseId
                (root.GetProperty("actorId").GetGuid())
                role
                (root.GetProperty("grantRevision").GetInt64())
                requestSha

        try
            if
                ticket.OperationId <> attemptId
                || attemptId <> WitnessEventIdentity.startEventId operationId ordinal
                || plain <> again
            then
                raise WitnessPending

            {
                Kind = "START"
                OperationId = operationId
                CaseId = caseId
                WitnessEventId = ticket.OperationId
                RequestSha256 = requestSha
                AttemptOrdinal = Some ordinal
            }
        finally
            CryptographicOperations.ZeroMemory(again)

    let readCandidate (witness: WitnessProtocol) (record: JournalRecord) =
        let ticket = record.Evidence.Ticket

        if ticket.Phase <> Intent then
            raise WitnessPending

        let plain =
            witness.KeyCustody.Decrypt(
                ticket.KeyId,
                witness.AssociatedData(ticket.OperationId, "INTENT"),
                record.Evidence.EncryptedPayload
            )

        try
            use document = JsonDocument.Parse(plain)
            let root = document.RootElement

            if root.GetProperty("version").GetInt32() <> 1 then
                raise WitnessPending

            match text root "kind" with
            | "PREPARE" -> parsePrepare ticket root plain
            | "START" -> parseStart ticket root plain
            | _ -> raise WitnessPending
        finally
            CryptographicOperations.ZeroMemory(plain)

    type WitnessProtocol with
        member this.ReadTechnicalCandidate(record: JournalRecord) = readCandidate this record

        member this.ReadTechnicalCandidate(eventId: Guid) =
            let evidence =
                this.EvidenceStore.TryReadEvidence(eventId, Intent)
                |> Option.defaultWith (fun () -> raise WitnessPending)

            readCandidate
                this
                {
                    Evidence = evidence
                    PreviousHash = Array.zeroCreate<byte> 32
                }
