namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// Owner-only signed adoption. Approval alone never changes copy state or proves deletion.
module internal ManagedCopyAdoptionOwner =
    let private validDocument (value: SignedCopyAdoptionDocument) =
        not (isNull (box value.Canonical))
        && value.Canonical.Length >= 2
        && value.Canonical.Length <= 16384
        && not (isNull (box value.Signature))
        && value.Signature.Length = 64

    let private valid (submission: CopyAdoptionSubmission) =
        submission.ApprovalId <> Guid.Empty
        && submission.AdoptionEventId <> Guid.Empty
        && validDocument submission.Custodian
        && validDocument submission.Registry
        && validDocument submission.Inspection

    let private existing ownerConnection eventId ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)
            let! found = ManagedCopyAdoptionOwnerRead.existing connection transaction eventId
            do! transaction.CommitAsync(ct)
            return found
        }

    let private audit ownerConnection witness commitments ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct
            return ()
        }

    let private stillValid now (ready: CopyAdoptionOwnerEventData) =
        now < ready.Approval.Request.ExpiresAt
        && now < ready.Documents.Custody.ValidUntil
        && now < ready.Documents.Registry.ValidUntil
        && now < ready.Documents.Inspection.ValidUntil
        && now < ready.PrivateLocationExpiresAt

    let private processLocked
        connection
        transaction
        (witness: WitnessProtocol)
        privateLocation
        actorRevision
        (stored: StoredCaseTombstone)
        (submission: CopyAdoptionSubmission)
        ct
        =
        task {
            let! prepared =
                ManagedCopyAdoptionOwnerChecks.prepare
                    connection
                    transaction
                    witness
                    privateLocation
                    actorRevision
                    stored
                    submission
                    ct

            match prepared with
            | Error refusal -> return refusal
            | Ok ready ->
                let! fresh = Sql.databaseNow connection transaction

                if not (stillValid fresh ready) then
                    return CopyAdoptionOwnerOutcome.ResourceUnavailable
                elif
                    witness.EvidenceStore
                        .TryReadMetadataOperation(submission.AdoptionEventId, Intent)
                        .IsSome
                then
                    return CopyAdoptionOwnerOutcome.Unconfirmed submission.AdoptionEventId
                else
                    return!
                        ManagedCopyAdoptionOwnerCommit.commit connection transaction witness ready
        }

    let private transact
        ownerConnection
        (witness: WitnessProtocol)
        privateLocation
        (submission: CopyAdoptionSubmission)
        ct
        =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            OwnerConnection.requireIdentity connection
            SchemaBaseline.requireCurrent connection
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
            let! actorRevision = ActorGrantRead.lockRevision connection transaction true ct

            let! approval =
                ManagedCopyAdoptionApprovalRead.find connection transaction submission.ApprovalId

            match approval with
            | None -> return CopyAdoptionOwnerOutcome.ResourceUnavailable
            | Some prior ->
                let! tombstone = CaseTombstoneRead.lock connection transaction prior.CaseId

                match tombstone with
                | None -> return CopyAdoptionOwnerOutcome.ResourceUnavailable
                | Some stored ->
                    return!
                        processLocked
                            connection
                            transaction
                            witness
                            privateLocation
                            actorRevision
                            stored
                            submission
                            ct
        }

    let adopt
        (ownerConnection: string)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (privateLocation: ICopyAdoptionPrivateLocation)
        (submission: CopyAdoptionSubmission)
        (ct: CancellationToken)
        =
        task {
            if not (valid submission) then
                return CopyAdoptionOwnerOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    commitments.Admit()
                    use fenceConnection = new NpgsqlConnection(ownerConnection)
                    do! fenceConnection.OpenAsync(ct)
                    OwnerConnection.requireIdentity fenceConnection
                    SchemaBaseline.requireCurrent fenceConnection

                    use! _authorityFence =
                        AuthorityOperationFence.acquireExclusive None fenceConnection ct

                    let! prior = existing ownerConnection submission.AdoptionEventId ct

                    match prior with
                    | Some accepted ->
                        return!
                            ManagedCopyAdoptionOwnerReplay.replay
                                ownerConnection
                                witness
                                commitments
                                submission
                                accepted
                                ct
                    | None ->
                        do! audit ownerConnection witness commitments ct

                        let! outcome =
                            transact ownerConnection witness privateLocation submission ct

                        match outcome with
                        | CopyAdoptionOwnerOutcome.Adopted _ ->
                            try
                                do! audit ownerConnection witness commitments ct
                                return outcome
                            with _ ->
                                return
                                    CopyAdoptionOwnerOutcome.Unconfirmed submission.AdoptionEventId
                        | _ -> return outcome
                with _ ->
                    return CopyAdoptionOwnerOutcome.AuditUnavailable "owner-admission"
        }
