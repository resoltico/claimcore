namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql

/// Exact owner invocation and retry reconciliation for independently verified copy deletion.
module internal ManagedCopyVerifiedDeletionExecution =
    let private replay
        connection
        transaction
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        stored
        approvalId
        =
        task {
            let! consumed =
                ManagedCopyVerifiedDeletion.used
                    connection
                    transaction
                    approvalId
                    transition.Copy.EventId

            if consumed then
                return!
                    ManagedCopyTransitionAdministration.exactRetry
                        witness
                        transition
                        canonical
                        signature
                        stored
            else
                return AuthorityWriteOutcome.Unconfirmed transition.Copy.EventId
        }

    let private underLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier)
        (transition: ManagedCopyTransition)
        canonical
        signature
        =
        task {
            let! _ =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! prior =
                ManagedCopyOwnerRead.transitionEvent connection transaction transition.Copy.EventId

            match prior, transition.DeletionApprovalId with
            | Some stored, Some approvalId ->
                return!
                    replay
                        connection
                        transaction
                        witness
                        transition
                        canonical
                        signature
                        stored
                        approvalId
            | None, Some _ ->
                return!
                    ManagedCopyVerifiedDeletion.newEvent
                        connection
                        transaction
                        witness
                        verifier
                        transition
                        canonical
                        signature
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let execute
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier)
        (canonical: byte array)
        (signature: byte array)
        =
        task {
            match ManagedCopyTransitionAttestation.parse canonical with
            | Some transition when
                transition.EventKind = "VERIFIED_DELETED"
                && not (isNull (box signature))
                && signature.Length = 64
                ->
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    do! witness.Admit(CancellationToken.None)

                    use! _authorityFence =
                        AuthorityOperationFence.acquireShared None connection CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                    return!
                        underLock
                            connection
                            transaction
                            witness
                            verifier
                            transition
                            canonical
                            signature
                with _ ->
                    return AuthorityWriteOutcome.Unconfirmed transition.Copy.EventId
            | _ -> return AuthorityWriteOutcome.Refused
        }
