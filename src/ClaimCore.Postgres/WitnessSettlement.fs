namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open ClaimCore.Witness

/// Exact settlement and committed-intent proof; it never mints a replacement identity.
module internal WitnessSettlement =
    let private verifySettlement
        (custody: IKeyCustody)
        associatedData
        operationId
        phase
        (intent: WitnessIntent)
        (evidence: Evidence)
        =
        let ticket = evidence.Ticket
        let aad = associatedData operationId (WitnessProof.settlementName phase)
        let plain = custody.Decrypt(ticket.KeyId, aad, evidence.EncryptedPayload)

        try
            if
                ticket.OperationId <> operationId
                || ticket.Phase <> phase
                || ticket.Epoch <> intent.Ticket.Epoch
                || ticket.KeyId <> intent.Ticket.KeyId
                || ticket.ScopeKind <> intent.Ticket.ScopeKind
                || ticket.SubjectCaseId <> intent.Ticket.SubjectCaseId
                || ticket.Sequence <= intent.Ticket.Sequence
                || plain <> intent.CandidateHash
            then
                raise WitnessPending

            ticket
        finally
            CryptographicOperations.ZeroMemory(plain)


    let authorityCiphertext
        (store: Store)
        (custody: IKeyCustody)
        associatedData
        (intent: WitnessIntent)
        (ct: CancellationToken)
        =
        task {
            let operationId = intent.Ticket.OperationId
            let aad = associatedData operationId "SETTLED_AUTHORITY"
            let! observed = store.TryReadEvidence(operationId, SettledAuthority, ct)

            match observed with
            | None -> return custody.Encrypt(intent.Ticket.KeyId, aad, intent.CandidateHash)
            | Some evidence ->
                verifySettlement custody associatedData operationId SettledAuthority intent evidence
                |> ignore

                return Array.copy evidence.EncryptedPayload
        }

    let settle
        (store: Store)
        (custody: IKeyCustody)
        associatedData
        beforeSettlement
        observer
        operationId
        phase
        (intent: WitnessIntent)
        =
        task {
            try
                let! existing = store.TryReadEvidence(operationId, phase, CancellationToken.None)

                match existing with
                | Some evidence ->
                    return verifySettlement custody associatedData operationId phase intent evidence
                | None ->
                    beforeSettlement ()
                    let aad = associatedData operationId (WitnessProof.settlementName phase)
                    let encrypted = custody.Encrypt(intent.Ticket.KeyId, aad, intent.CandidateHash)

                    try
                        return!
                            store.Append(
                                operationId,
                                None,
                                phase,
                                intent.Ticket.KeyId,
                                encrypted,
                                CancellationToken.None
                            )
                    finally
                        CryptographicOperations.ZeroMemory(encrypted)
            with error ->
                WitnessFailures.report observer WitnessFailureStage.Settlement error

                try
                    let! observed =
                        store.TryReadEvidence(operationId, phase, CancellationToken.None)

                    return
                        observed
                        |> Option.defaultWith (fun () -> raise WitnessPending)
                        |> verifySettlement custody associatedData operationId phase intent
                with readError ->
                    WitnessFailures.report observer WitnessFailureStage.Settlement readError
                    return raise WitnessPending
        }

    let requireSettled
        (store: Store)
        (custody: IKeyCustody)
        associatedData
        operationId
        settlement
        (ct: CancellationToken)
        =
        task {
            let! retainedIntent = store.TryReadEvidence(operationId, Intent, ct)
            let intent = retainedIntent |> Option.defaultWith (fun () -> raise WitnessPending)

            let! retainedOutcome = store.TryReadEvidence(operationId, settlement, ct)
            let outcome = retainedOutcome |> Option.defaultWith (fun () -> raise WitnessPending)

            if outcome.Ticket.Sequence <= intent.Ticket.Sequence then
                raise WitnessPending

            let first =
                custody.Decrypt(
                    intent.Ticket.KeyId,
                    associatedData operationId "INTENT",
                    intent.EncryptedPayload
                )

            try
                let second =
                    custody.Decrypt(
                        outcome.Ticket.KeyId,
                        associatedData operationId (WitnessProof.settlementName settlement),
                        outcome.EncryptedPayload
                    )

                try
                    if SHA256.HashData(first) <> second then
                        raise WitnessPending
                finally
                    CryptographicOperations.ZeroMemory(second)
            finally
                CryptographicOperations.ZeroMemory(first)
        }
