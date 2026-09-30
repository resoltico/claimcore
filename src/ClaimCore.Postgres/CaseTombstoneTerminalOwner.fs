namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application

/// Owner-only terminal transition. Draft approvals never call this path; signed copy and
/// recovery-fence issuers are mandatory and may return None to preserve ERASURE_PENDING.
module internal CaseTombstoneTerminalOwner =
    let private readExisting ownerConnection eventId =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync()
            use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)
            let! found = CaseTombstoneTerminalEventRead.find connection transaction eventId
            do! transaction.CommitAsync()
            return found
        }

    let private preflightAudit ownerConnection witness commitments ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct
            return ()
        }

    let private databaseClock connection transaction =
        task {
            use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? DateTimeOffset as instant -> instant
                | :? DateTime as instant when instant.Kind = DateTimeKind.Utc ->
                    DateTimeOffset instant
                | _ -> raise (InvalidDataException("Primary clock is unavailable."))
        }

    let private processNew
        connection
        transaction
        (witness: WitnessProtocol)
        copyProvider
        fenceProvider
        actorRevision
        (stored: StoredTerminalTombstone)
        proposal
        ct
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal

            if
                witness.EvidenceStore
                    .TryReadEvidence(value.EventId, ClaimCore.Witness.Intent)
                    .IsSome
            then
                return OwnerTerminalOutcome.Unconfirmed value.EventId
            else
                let! instant = databaseClock connection transaction

                let! prepared =
                    CaseTombstoneTerminalOwnerPrepare.prepare
                        connection
                        transaction
                        witness
                        copyProvider
                        fenceProvider
                        actorRevision
                        stored
                        proposal
                        instant
                        ct

                match prepared with
                | Error refusal -> return refusal
                | Ok ready ->
                    return!
                        CaseTombstoneTerminalOwnerCommit.commit
                            connection
                            transaction
                            witness
                            actorRevision
                            proposal
                            ready
                            ct
        }

    let private transact
        ownerConnection
        witness
        commitments
        copyProvider
        fenceProvider
        proposal
        ct
        =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
            let! actorRevision = ActorGrantRead.lockRevision connection transaction true ct
            let value = TombstoneTerminalProposal.copy proposal

            let! existing =
                CaseTombstoneTerminalEventRead.find connection transaction value.EventId

            match existing with
            | Some accepted ->
                do! transaction.CommitAsync(ct)

                return!
                    CaseTombstoneTerminalOwnerReplay.run
                        ownerConnection
                        witness
                        commitments
                        proposal
                        accepted
                        ct
            | None ->
                let! found = CaseTombstoneTerminalRead.lock connection transaction value.CaseId

                match found with
                | None -> return OwnerTerminalOutcome.ResourceUnavailable
                | Some stored ->
                    return!
                        processNew
                            connection
                            transaction
                            witness
                            copyProvider
                            fenceProvider
                            actorRevision
                            stored
                            proposal
                            ct
        }

    let private advanceWithAudit
        ownerConnection
        witness
        commitments
        copyProvider
        fenceProvider
        proposal
        ct
        =
        task {
            do! preflightAudit ownerConnection witness commitments ct

            let! outcome =
                transact ownerConnection witness commitments copyProvider fenceProvider proposal ct

            match outcome with
            | OwnerTerminalOutcome.Advanced _ ->
                try
                    do! preflightAudit ownerConnection witness commitments ct
                    return outcome
                with _ ->
                    return
                        OwnerTerminalOutcome.Unconfirmed(TombstoneTerminalProposal.eventId proposal)
            | _ -> return outcome
        }

    let execute
        ownerConnection
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (copyProvider: ICopyErasureCertification)
        (fenceProvider: IRecoveryFenceCertification)
        proposal
        (ct: CancellationToken)
        =
        task {
            let eventId = TombstoneTerminalProposal.eventId proposal

            try
                witness.Admit()
                commitments.Admit()
                use fenceConnection = new NpgsqlConnection(ownerConnection)
                do! fenceConnection.OpenAsync(ct)
                OwnerConnection.requireIdentity fenceConnection
                SchemaBaseline.requireCurrent fenceConnection

                use! _authorityFence =
                    AuthorityOperationFence.acquireExclusive None fenceConnection ct

                let! existing = readExisting ownerConnection eventId

                match existing with
                | Some accepted ->
                    return!
                        CaseTombstoneTerminalOwnerReplay.run
                            ownerConnection
                            witness
                            commitments
                            proposal
                            accepted
                            ct
                | None ->
                    return!
                        advanceWithAudit
                            ownerConnection
                            witness
                            commitments
                            copyProvider
                            fenceProvider
                            proposal
                            ct
            with _ ->
                return OwnerTerminalOutcome.Unconfirmed eventId
        }
