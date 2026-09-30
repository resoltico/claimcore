namespace ClaimCore.Postgres

open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type private SettlementContext =
    {
        DataSource: NpgsqlDataSource
        OwnerWitnessConnection: string
        Witness: WitnessProtocol
        Verifier: IWriterHandoffEvidenceVerifier
        Commitments: ISuppressionCommitments option
        Canonical: byte array
        Signature: byte array
        OldCapability: byte array
        NewCapability: byte array
    }

/// Owner-only COMMIT of one exact pending witnessed handoff. Product composition must supply
/// independently recomputed restore, archive, publication and old-host isolation evidence.
module internal WriterHandoffOwnerSettlement =
    let private complete
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (context: SettlementContext)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        =
        match
            WriterHandoffOwnerReconcile.trySettled
                owner
                transaction
                context.Witness
                prepared
                value
                context.Canonical
                context.Signature
        with
        | Some ticket ->
            WriterHandoffOwnerSettlementCompletion.existing
                owner
                transaction
                context.DataSource
                context.Witness
                context.Commitments
                prepared
                value
                context.Canonical
                context.Signature
                ticket
        | None ->
            WriterHandoffOwnerSettlementCompletion.fresh
                owner
                transaction
                context.DataSource
                context.OwnerWitnessConnection
                context.Witness
                context.Verifier
                context.Commitments
                prepared
                value
                context.Canonical
                context.Signature
                context.OldCapability
                context.NewCapability

    let private underLock
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (context: SettlementContext)
        (value: WriterHandoffSettlement)
        (started: bool ref)
        =
        task {
            let! _ = ActorGrantRead.lockRevision owner transaction true CancellationToken.None

            match
                WriterHandoffOwnerRead.preparation owner transaction context.Witness value.HandoffId
            with
            | None ->
                return
                    if context.Witness.EvidenceStore.TryReadHandoff(value.HandoffId).IsSome then
                        WriterHandoffOwnerOutcome.Unconfirmed value.HandoffId
                    else
                        WriterHandoffOwnerOutcome.Refused
            | Some prepared ->
                started.Value <- true
                return! complete owner transaction context prepared value
        }

    let commit
        (primaryOwner: NpgsqlConnection)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (verifier: IWriterHandoffEvidenceVerifier)
        (commitments: ISuppressionCommitments option)
        (canonical: byte array)
        (signature: byte array)
        (oldCapability: byte array)
        (newCapability: byte array)
        =
        task {
            match WriterHandoffSettlement.parse canonical with
            | None -> return WriterHandoffOwnerOutcome.Refused
            | Some value ->
                let started = ref false

                try
                    OwnerConnection.requireIdentity primaryOwner
                    SchemaBaseline.requireCurrent primaryOwner
                    witness.AdmitReadOnly()

                    use! _authorityFence =
                        AuthorityOperationFence.acquireExclusive
                            None
                            primaryOwner
                            CancellationToken.None

                    use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

                    let context =
                        {
                            DataSource = dataSource
                            OwnerWitnessConnection = ownerWitnessConnection
                            Witness = witness
                            Verifier = verifier
                            Commitments = commitments
                            Canonical = canonical
                            Signature = signature
                            OldCapability = oldCapability
                            NewCapability = newCapability
                        }

                    return! underLock primaryOwner transaction context value started
                with _ ->
                    return
                        if started.Value then
                            WriterHandoffOwnerOutcome.Unconfirmed value.HandoffId
                        else
                            WriterHandoffOwnerOutcome.Refused
        }
