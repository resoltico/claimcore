namespace ClaimCore.Postgres

open System
open System.Threading
open System.Data.Common
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

module internal WitnessProtocolReconciliation =
    let private acceptedIntent
        (witness: WitnessProtocol)
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operationId
        (ct: CancellationToken)
        =
        task {
            let store = witness.EvidenceStore
            let custody = witness.KeyCustody

            let associatedData operation phase =
                witness.AssociatedData(operation, phase)

            let! retained = store.TryReadEvidence(operationId, Intent, ct)
            let intent = retained |> Option.defaultWith (fun () -> raise WitnessPending)

            let plain =
                custody.Decrypt(
                    intent.Ticket.KeyId,
                    associatedData operationId "INTENT",
                    intent.EncryptedPayload
                )

            try
                use document = JsonDocument.Parse(plain)
                let root = document.RootElement

                do!
                    WitnessPrimaryMatch.requireAccepted
                        connection
                        transaction
                        operationId
                        intent.Ticket
                        root
                        ct

                return
                    {
                        Ticket = intent.Ticket
                        CandidateHash = SHA256.HashData(plain)
                    }
            finally
                CryptographicOperations.ZeroMemory(plain)
        }

    type WitnessProtocol with
        member this.VerifyHistoricalTip
            (sequence: int64, expectedHash: byte array, ct: CancellationToken)
            =
            task {
                if isNull (box expectedHash) || expectedHash.Length <> 32 then
                    raise WitnessPending

                let! observed = this.EvidenceStore.TryReadEntryHash(sequence, ct)

                match observed with
                | Some hash when
                    CryptographicOperations.FixedTimeEquals(hash.AsSpan(), expectedHash.AsSpan())
                    ->
                    return ()
                | _ -> return raise WitnessPending
            }

        /// Caller must obtain these values from a committed primary authority row under its lock.
        member this.ReconcileAuthority
            (
                operationId: Guid,
                sequence: int64,
                epoch: int64,
                entryHash: byte array,
                exactCanonicalAction: byte array,
                ct: CancellationToken
            ) =
            task {
                let! retained = this.EvidenceStore.TryReadEvidence(operationId, Intent, ct)
                let intent = retained |> Option.defaultWith (fun () -> raise WitnessPending)

                if
                    intent.Ticket.Sequence <> sequence
                    || intent.Ticket.Epoch <> epoch
                    || intent.Ticket.EntryHash <> entryHash
                then
                    raise WitnessPending

                let plain =
                    this.KeyCustody.Decrypt(
                        intent.Ticket.KeyId,
                        this.AssociatedData(operationId, "INTENT"),
                        intent.EncryptedPayload
                    )

                try
                    if plain <> exactCanonicalAction then
                        raise WitnessPending

                    let recovered =
                        {
                            Ticket = intent.Ticket
                            CandidateHash = SHA256.HashData(plain)
                        }

                    let! _ = this.SettleAuthority(operationId, recovered)

                    do!
                        this.VerifyAuthorityEvidence(
                            operationId,
                            sequence,
                            epoch,
                            entryHash,
                            recovered.CandidateHash,
                            ct
                        )
                finally
                    CryptographicOperations.ZeroMemory(plain)
            }

        member this.VerifyAccepted
            (
                connection: NpgsqlConnection,
                transaction: NpgsqlTransaction,
                operationId: Guid,
                ct: CancellationToken
            ) =
            task {
                let! intent = acceptedIntent this connection transaction operationId ct

                do!
                    this.VerifyAcceptedEvidence(
                        operationId,
                        intent.Ticket.Sequence,
                        intent.Ticket.Epoch,
                        intent.Ticket.EntryHash,
                        intent.CandidateHash,
                        ct
                    )
            }

        member this.ReconcileAccepted
            (
                connection: NpgsqlConnection,
                transaction: NpgsqlTransaction,
                operationId: Guid,
                ct: CancellationToken
            ) =
            task {
                try
                    let! intent = acceptedIntent this connection transaction operationId ct
                    let! _ = this.SettleAccepted(operationId, intent)

                    do!
                        this.VerifyAcceptedEvidence(
                            operationId,
                            intent.Ticket.Sequence,
                            intent.Ticket.Epoch,
                            intent.Ticket.EntryHash,
                            intent.CandidateHash,
                            CancellationToken.None
                        )
                with
                | :? OperationCanceledException -> return raise (OperationCanceledException(ct))
                | _ -> return raise WitnessPending
            }

        member this.ReconcileRevoked
            (
                connection: NpgsqlConnection,
                transaction: NpgsqlTransaction,
                operationId: Guid,
                ct: CancellationToken
            ) =
            task {
                let eventId = WitnessEventIdentity.revocationEventId operationId
                let store = this.EvidenceStore
                let custody = this.KeyCustody
                let associatedData operation phase = this.AssociatedData(operation, phase)

                let! retained = store.TryReadEvidence(eventId, Intent, ct)
                let intent = retained |> Option.defaultWith (fun () -> raise WitnessPending)

                let plain =
                    custody.Decrypt(
                        intent.Ticket.KeyId,
                        associatedData eventId "INTENT",
                        intent.EncryptedPayload
                    )

                try
                    do!
                        WitnessPrimaryMatch.requireRevoked
                            connection
                            transaction
                            operationId
                            eventId
                            intent.Ticket
                            plain
                            ct

                    let recovered =
                        {
                            Ticket = intent.Ticket
                            CandidateHash = SHA256.HashData(plain)
                        }

                    let! _ = this.SettleRevoked(eventId, recovered)
                    do! this.RequireSettled(eventId, SettledRevoked, CancellationToken.None)
                finally
                    CryptographicOperations.ZeroMemory(plain)
            }
