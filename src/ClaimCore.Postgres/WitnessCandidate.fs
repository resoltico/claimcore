namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Witness

exception internal WitnessPending

type internal WitnessIntent =
    {
        Ticket: Ticket
        CandidateHash: byte array
    }

type internal AcceptedActorEvidence =
    {
        CaseId: Guid
        PreparerActorId: Guid
        ImporterActorId: Guid option
        SubmitterActorId: Guid option
        ResolverActorId: Guid option
        AcceptedActorId: Guid
        GrantRevision: int64
    }

/// Pure versioned candidate encoding shared by the writer and the historical row auditor.
module internal WitnessCandidate =
    let actorEvidence caseId (attribution: ExecutionAttribution) =
        let actorId = attribution.Command.Actor.ActorId

        let submitter, resolver =
            match attribution.Phase with
            | AttemptActorPhase.NormalSubmit -> Some actorId, None
            | AttemptActorPhase.RecoveryResolve -> None, Some actorId

        {
            CaseId = caseId
            PreparerActorId = attribution.PreparerActorId
            ImporterActorId = attribution.ImporterActorId
            SubmitterActorId = submitter
            ResolverActorId = resolver
            AcceptedActorId = actorId
            GrantRevision = attribution.Command.Actor.GrantRevision
        }

    let private validActorEvidence evidence =
        evidence.CaseId <> Guid.Empty
        && evidence.PreparerActorId <> Guid.Empty
        && evidence.ImporterActorId <> Some Guid.Empty
        && evidence.AcceptedActorId <> Guid.Empty
        && evidence.GrantRevision > 0L
        && match evidence.SubmitterActorId, evidence.ResolverActorId with
           | Some submitter, None -> submitter = evidence.AcceptedActorId
           | None, Some resolver -> resolver = evidence.AcceptedActorId
           | _ -> false

    let private microsecondInstant (value: DateTimeOffset) =
        DateTimeOffset(value.UtcTicks - value.UtcTicks % 10L, TimeSpan.Zero)

    let accepted
        (operationId: Guid)
        (evidence: AcceptedActorEvidence)
        (caseReference: string)
        (expectedRevision: int64)
        (newRevision: int64)
        (commandName: string)
        (businessDate: DateOnly)
        (observedInstant: DateTimeOffset)
        (canonicalRequest: byte array)
        (canonicalSnapshot: byte array)
        =
        if not (validActorEvidence evidence) then
            invalidArg (nameof evidence) "Witness actor evidence is invalid."

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 2)
        writer.WriteString("operationId", operationId)
        writer.WriteString("caseId", evidence.CaseId)
        writer.WriteString("preparerActorId", evidence.PreparerActorId)

        match evidence.ImporterActorId with
        | Some value -> writer.WriteString("importerActorId", value)
        | None -> writer.WriteNull("importerActorId")

        match evidence.SubmitterActorId with
        | Some value -> writer.WriteString("submitterActorId", value)
        | None -> writer.WriteNull("submitterActorId")

        match evidence.ResolverActorId with
        | Some value -> writer.WriteString("resolverActorId", value)
        | None -> writer.WriteNull("resolverActorId")

        writer.WriteString("acceptedActorId", evidence.AcceptedActorId)
        writer.WriteNumber("grantRevision", evidence.GrantRevision)
        writer.WriteString("caseReference", caseReference)
        writer.WriteNumber("expectedRevision", expectedRevision)
        writer.WriteNumber("newRevision", newRevision)
        writer.WriteString("command", commandName)
        writer.WriteString("effectiveBusinessDate", businessDate.ToString("yyyy-MM-dd"))
        writer.WriteString("observedUtcInstant", (microsecondInstant observedInstant).ToString("O"))
        writer.WriteNumber("ruleRevision", DomainRules.version)
        writer.WriteBase64String("canonicalRequest", ReadOnlySpan<byte>(canonicalRequest))
        writer.WriteBase64String("canonicalSnapshot", ReadOnlySpan<byte>(canonicalSnapshot))
        writer.WriteEndObject()
        writer.Flush()
        let result = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        result

    let acceptedDigestFromEvidence
        operationId
        evidence
        caseReference
        expectedRevision
        newRevision
        commandName
        businessDate
        observedInstant
        canonicalRequest
        canonicalSnapshot
        =
        let bytes =
            accepted
                operationId
                evidence
                caseReference
                expectedRevision
                newRevision
                commandName
                businessDate
                observedInstant
                canonicalRequest
                canonicalSnapshot

        try
            SHA256.HashData(bytes)
        finally
            CryptographicOperations.ZeroMemory(bytes)

    let revoked (operationId: Guid) (requestSha256: string) (evidence: RevocationActorEvidence) =
        if
            operationId = Guid.Empty
            || evidence.CaseId = Guid.Empty
            || evidence.RevokingActorId = Guid.Empty
            || evidence.GrantRevision <= 0L
        then
            invalidArg (nameof evidence) "Revocation actor evidence is invalid."

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 2)
        writer.WriteString("kind", "REVOCATION")
        writer.WriteString("operationId", operationId)
        writer.WriteString("caseId", evidence.CaseId)
        writer.WriteString("requestSha256", requestSha256)
        writer.WriteString("revokingActorId", evidence.RevokingActorId)
        writer.WriteNumber("grantRevision", evidence.GrantRevision)
        writer.WriteEndObject()
        writer.Flush()
        let result = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        result

    let revokedDigestFromEvidence operationId requestSha256 evidence =
        let bytes = revoked operationId requestSha256 evidence

        try
            SHA256.HashData(bytes)
        finally
            CryptographicOperations.ZeroMemory(bytes)
