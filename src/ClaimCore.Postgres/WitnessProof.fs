namespace ClaimCore.Postgres

open System
open System.Threading
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

    let private withPlain
        (custody: IKeyCustody)
        identity
        operation
        phase
        (evidence: Evidence)
        validate
        =
        let plain =
            custody.Decrypt(
                evidence.Ticket.KeyId,
                associatedData identity operation phase,
                evidence.EncryptedPayload
            )

        try
            validate plain
        finally
            CryptographicOperations.ZeroMemory(plain)

    let private requireBinding operationId settlement epoch (intent: Evidence, outcome: Evidence) =
        if
            intent.Ticket.OperationId <> operationId
            || outcome.Ticket.OperationId <> operationId
            || intent.Ticket.Phase <> Intent
            || outcome.Ticket.Phase <> settlement
            || outcome.Ticket.Epoch <> epoch
        then
            raise WitnessPending

    /// One verifier for individually acquired and batched independently witnessed subjects.
    let verifyLoaded
        (custody: IKeyCustody)
        (identity: Identity)
        operationId
        settlement
        sequence
        epoch
        (entryHash: byte array)
        (candidateDigest: byte array)
        (intent: Evidence, outcome: Evidence)
        =
        if candidateDigest.Length <> 32 || entryHash.Length <> 32 then
            raise WitnessPending

        requireBinding operationId settlement epoch (intent, outcome)

        if
            intent.Ticket.Sequence <> sequence
            || intent.Ticket.Epoch <> epoch
            || intent.Ticket.EntryHash <> entryHash
            || outcome.Ticket.Sequence <= intent.Ticket.Sequence
        then
            raise WitnessPending

        withPlain custody identity operationId "INTENT" intent (fun plain ->
            if SHA256.HashData(plain) <> candidateDigest then
                raise WitnessPending)

        withPlain custody identity operationId (settlementName settlement) outcome (fun settled ->
            if settled <> candidateDigest then
                raise WitnessPending)

        intent.Ticket, outcome.Ticket

    let verifyEvidence
        (store: Store)
        (custody: IKeyCustody)
        (identity: Identity)
        operationId
        settlement
        sequence
        epoch
        entryHash
        candidateDigest
        (ct: CancellationToken)
        =
        task {
            let! retainedIntent = store.TryReadEvidence(operationId, Intent, ct)
            let! retainedOutcome = store.TryReadEvidence(operationId, settlement, ct)
            let required = Option.defaultWith (fun () -> raise WitnessPending)

            return
                verifyLoaded
                    custody
                    identity
                    operationId
                    settlement
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    (required retainedIntent, required retainedOutcome)
        }

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
