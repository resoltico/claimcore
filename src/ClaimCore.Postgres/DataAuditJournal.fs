namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

module internal DataAuditJournal =
    type private ScanState =
        {
            mutable After: int64
            mutable PreviousHash: byte array
            mutable Entries: int64
            mutable Intents: int64
            mutable Settled: int64
            mutable CurrentKeyId: Guid
            mutable LossIntentObserved: bool
            mutable LossSettlementObserved: bool
        }

    let private requirePrimary
        (command: NpgsqlCommand)
        operationId
        (cancellationToken: CancellationToken)
        =
        task {
            command.Parameters["operation"].Value <- operationId
            let! value = command.ExecuteScalarAsync(cancellationToken)

            if not (unbox<bool> value) then
                corrupt ()
        }

    let private sealedBeforePurge (command: NpgsqlCommand) (ticket: Ticket) ct =
        task {
            match ticket.ScopeKind, ticket.SubjectCaseId with
            | Case, Some caseId ->
                command.Parameters["case"].Value <- caseId
                command.Parameters["sequence"].Value <- ticket.Sequence
                let! found = command.ExecuteScalarAsync(ct)
                return unbox<bool> found
            | _ -> return false
        }

    let private rotate (witness: WitnessProtocol) (state: ScanState) (item: MetadataRecord) =
        let ticket = item.Ticket

        let evidence =
            witness.EvidenceStore.TryReadEvidence(ticket.OperationId, KeyRotated)
            |> Option.defaultWith corrupt

        if evidence.Ticket <> ticket then
            corrupt ()

        state.CurrentKeyId <-
            witnessProof (fun () ->
                witness.VerifyKeyRotated(
                    {
                        Evidence = evidence
                        PreviousHash = item.PreviousHash
                    },
                    state.CurrentKeyId
                ))

    let private settledAuthority witness (queries: DataAuditJournalCommands) (ticket: Ticket) ct =
        task {
            queries.Authority.Parameters["operation"].Value <- ticket.OperationId
            let! found = queries.Authority.ExecuteScalarAsync(ct)

            if not (unbox<bool> found) then
                do!
                    DataAuditJournalTechnical.verify
                        witness
                        queries.Technical
                        queries.Terminal
                        ticket
                        ct
        }

    let private verifyAuthorityPhase
        witness
        (queries: DataAuditJournalCommands)
        (ticket: Ticket)
        sealedCase
        ct
        =
        task {
            if not sealedCase then
                do! settledAuthority witness queries ticket ct
            else
                let! required =
                    DataAuditJournalExternalPublication.requiresReceipt
                        queries.ExternalPublicationMarker
                        witness
                        ticket
                        ct

                if required then
                    do! requirePrimary queries.ExternalPublication ticket.OperationId ct
        }

    let private lossIntent connection transaction witness tip state (ticket: Ticket) ct =
        state.Intents <- state.Intents + 1L

        if tip.LossRetirementId = Some ticket.OperationId then
            if state.LossIntentObserved then
                corrupt ()

            DataAuditInstallationLoss.verify connection transaction witness tip ticket ct
            state.LossIntentObserved <- true

    let private lossSettlement connection transaction witness tip state (ticket: Ticket) ct =
        if tip.LossRetirementId = Some ticket.OperationId then
            if state.LossSettlementObserved then
                corrupt ()

            DataAuditInstallationLoss.verify connection transaction witness tip ticket ct
            state.LossSettlementObserved <- true
            true
        else
            false

    let private verifyPhaseKind
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (state: ScanState)
        (queries: DataAuditJournalCommands)
        (item: MetadataRecord)
        sealedCase
        (cancellationToken: CancellationToken)
        =
        task {
            let ticket = item.Ticket

            match ticket.Phase with
            | Intent -> lossIntent connection transaction witness tip state ticket cancellationToken
            | SettledAccepted ->
                state.Settled <- state.Settled + 1L

                if not sealedCase then
                    do! requirePrimary queries.Accepted ticket.OperationId cancellationToken
            | SettledRevoked ->
                state.Settled <- state.Settled + 1L

                if not sealedCase then
                    do! requirePrimary queries.Revoked ticket.OperationId cancellationToken
            | AbortedBeforeCommit ->
                state.Settled <- state.Settled + 1L
                do! DataAuditJournalAbort.verify tip queries.Abort ticket cancellationToken
            | KeyRotated -> rotate witness state item
            | SettledAuthority ->
                state.Settled <- state.Settled + 1L

                if
                    not (
                        lossSettlement
                            connection
                            transaction
                            witness
                            tip
                            state
                            ticket
                            cancellationToken
                    )
                then
                    do! verifyAuthorityPhase witness queries ticket sealedCase cancellationToken
        }

    let private verifyPhase
        connection
        transaction
        witness
        tip
        state
        queries
        (item: MetadataRecord)
        cancellationToken
        =
        task {
            let ticket = item.Ticket

            if ticket.Epoch <> tip.Identity.Epoch then
                corrupt ()

            if ticket.Phase <> KeyRotated && ticket.KeyId <> state.CurrentKeyId then
                corrupt ()

            let! sealedCase = sealedBeforePurge queries.SealedCase ticket cancellationToken

            do!
                verifyPhaseKind
                    connection
                    transaction
                    witness
                    tip
                    state
                    queries
                    item
                    sealedCase
                    cancellationToken
        }

    let private scanPage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (state: ScanState)
        (queries: DataAuditJournalCommands)
        (cancellationToken: CancellationToken)
        =
        task {
            let page =
                witnessProof (fun () ->
                    witness.EvidenceStore.ReadMetadataPage(
                        state.After,
                        state.PreviousHash,
                        tip.TipSequence,
                        32
                    ))

            if page.Items.IsEmpty then
                corrupt ()

            for item in page.Items do
                do! DataAuditJournalPruned.require queries.Pruned item cancellationToken

                do!
                    verifyPhase
                        connection
                        transaction
                        witness
                        tip
                        state
                        queries
                        item
                        cancellationToken

                let ticket = item.Ticket
                state.After <- ticket.Sequence
                state.PreviousHash <- ticket.EntryHash
                state.Entries <- state.Entries + 1L
        }

    let private scanPages
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (queries: DataAuditJournalCommands)
        (cancellationToken: CancellationToken)
        =
        task {
            let state =
                {
                    After = 0L
                    PreviousHash = Array.zeroCreate<byte> 32
                    Entries = 0L
                    Intents = 0L
                    Settled = 0L
                    CurrentKeyId = tip.InitialKeyId
                    LossIntentObserved = false
                    LossSettlementObserved = false
                }

            while state.After < tip.TipSequence do
                do! scanPage connection transaction witness tip state queries cancellationToken

            if
                state.After <> tip.TipSequence
                || state.PreviousHash <> tip.TipHash
                || state.CurrentKeyId <> tip.ActiveKeyId
                || state.Settled > state.Intents
                || (tip.LossRetirementPending
                    && (not state.LossIntentObserved || state.LossSettlementObserved))
                || (tip.LossRetired
                    && (not state.LossIntentObserved || not state.LossSettlementObserved))
                || (tip.LossRetirementId.IsNone
                    && (state.LossIntentObserved || state.LossSettlementObserved))
            then
                corrupt ()

            let unresolved = state.Intents - state.Settled
            return state.Entries, if tip.HandoffPending then max 1L unresolved else unresolved
        }

    let scan
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (cancellationToken: CancellationToken)
        =
        task {
            use queries = DataAuditJournalCommands.create connection transaction
            return! scanPages connection transaction witness tip queries cancellationToken
        }
