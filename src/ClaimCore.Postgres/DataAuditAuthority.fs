namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Witness

module internal DataAuditAuthority =
    let private verifyHandoffs connection transaction witness cutoff cancellationToken =
        task {
            DataAuditInstallationUse.verify connection transaction witness cutoff

            let! handoffs =
                DataAuditWriterHandoffs.verify
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken

            let! activations =
                DataAuditWriterActivations.verify
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken

            let! preparations =
                DataAuditWriterHandoffPreparations.verify
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken

            let! aborts =
                DataAuditWriterHandoffAborts.verify
                    connection
                    transaction
                    witness
                    cutoff
                    cancellationToken

            return preparations, handoffs, activations, aborts
        }

    let private verifyCustody connection transaction witness cutoff cancellationToken =
        DataAuditCustody.verify connection transaction witness cutoff cancellationToken

    let private verifyActors connection transaction projection cancellationToken =
        DataAuditAuthorityProjection.verify connection transaction projection cancellationToken

    let private counts
        revision
        (actors, grants)
        (
            signerApprovals,
            signerKeys,
            signerEvents,
            ownerCopies,
            physicalVerifications,
            deletionApprovals,
            handoffApprovals,
            exports
        )
        (preparations, handoffs, activations, aborts)
        (entries, pending)
        : AuthorityAuditCounts =
        {
            AuthorityEvents = revision
            Actors = actors
            Grants = grants
            SignerApprovals = signerApprovals
            SignerKeys = signerKeys
            SignerEvents = signerEvents
            OwnerManagedCopies = ownerCopies
            CopyPhysicalVerifications = physicalVerifications
            CopyDeletionApprovals = deletionApprovals
            WriterHandoffApprovals = handoffApprovals
            WriterHandoffPreparations = preparations
            WriterHandoffs = handoffs
            WriterActivations = activations
            WriterHandoffAborts = aborts
            ManagedExports = exports
            WitnessEntries = entries
            PendingIntents = pending
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (cancellationToken: CancellationToken)
        =
        task {
            let! projection =
                DataAuditWitness.verifyAuthorityEvents
                    connection
                    transaction
                    witness
                    tip.TipSequence
                    cancellationToken

            let! actors = verifyActors connection transaction projection cancellationToken

            let! custody =
                verifyCustody connection transaction witness tip.TipSequence cancellationToken

            let! handoffs =
                verifyHandoffs connection transaction witness tip.TipSequence cancellationToken

            let! journal =
                DataAuditJournal.scan connection transaction witness tip cancellationToken

            return counts projection.Revision actors custody handoffs journal
        }
