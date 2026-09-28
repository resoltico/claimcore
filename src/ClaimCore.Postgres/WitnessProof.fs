namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Witness

/// Exact digest and ciphertext proof shared by witness admission and full audit.
module internal WitnessProof =
    let associatedData (identity: Identity) (operation: Guid) phase =
        Encoding.UTF8.GetBytes(
            identity.InstallationId.ToString("D")
            + ":"
            + identity.LineageId.ToString("D")
            + ":"
            + identity.Epoch.ToString(Globalization.CultureInfo.InvariantCulture)
            + ":"
            + operation.ToString("D")
            + ":"
            + phase
        )

    let settlementName =
        function
        | SettledAccepted -> "SETTLED_ACCEPTED"
        | SettledRevoked -> "SETTLED_REVOKED"
        | SettledAuthority -> "SETTLED_AUTHORITY"
        | _ -> invalidOp "Witness settlement phase is invalid."

    let candidate
        (operation: PreparedOperation)
        (context: BusinessContext)
        caseId
        (attribution: ExecutionAttribution)
        claim
        =
        let request = Operation.request operation
        let snapshot = Claim.view claim
        let evidence = WitnessCandidate.actorEvidence caseId attribution

        WitnessCandidate.accepted
            request.OperationId
            evidence
            request.CaseReference
            request.ExpectedVersion
            snapshot.Version
            (Commands.name request.Command)
            context.EffectiveBusinessDate
            context.ObservedUtcInstant
            (Operation.canonicalRequest operation)
            (CaseRecord.encodeSnapshot snapshot)

    let verifyEvidence
        (store: Store)
        (custody: IKeyCustody)
        (identity: Identity)
        (operationId: Guid)
        (settlement: Phase)
        (sequence: int64)
        (epoch: int64)
        (entryHash: byte array)
        (candidateDigest: byte array)
        =
        if candidateDigest.Length <> 32 || entryHash.Length <> 32 then
            raise WitnessPending

        let intent =
            store.TryReadEvidence(operationId, Intent)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        let outcome =
            store.TryReadEvidence(operationId, settlement)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        if
            intent.Ticket.Sequence <> sequence
            || intent.Ticket.Epoch <> epoch
            || intent.Ticket.EntryHash <> entryHash
            || outcome.Ticket.Sequence <= intent.Ticket.Sequence
        then
            raise WitnessPending

        let plain =
            custody.Decrypt(
                intent.Ticket.KeyId,
                associatedData identity operationId "INTENT",
                intent.EncryptedPayload
            )

        try
            if SHA256.HashData(plain) <> candidateDigest then
                raise WitnessPending

            let settled =
                custody.Decrypt(
                    outcome.Ticket.KeyId,
                    associatedData identity operationId (settlementName settlement),
                    outcome.EncryptedPayload
                )

            try
                if settled <> candidateDigest then
                    raise WitnessPending
            finally
                CryptographicOperations.ZeroMemory(settled)
        finally
            CryptographicOperations.ZeroMemory(plain)

        intent.Ticket, outcome.Ticket

    let requireScope expectedSubject (intent: Ticket, outcome: Ticket) =
        let expectedKind =
            match expectedSubject with
            | Some caseId when caseId <> Guid.Empty -> Case
            | None -> Installation
            | _ -> raise WitnessPending

        if
            intent.ScopeKind <> expectedKind
            || outcome.ScopeKind <> expectedKind
            || intent.SubjectCaseId <> expectedSubject
            || outcome.SubjectCaseId <> expectedSubject
        then
            raise WitnessPending
