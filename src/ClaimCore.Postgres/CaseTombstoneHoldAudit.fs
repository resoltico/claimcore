namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open DataAuditCommon

/// Replays the tombstone-only authority chain and active-hold projection without claimant rows.
module internal CaseTombstoneHoldAudit =
    [<NoEquality; NoComparison>]
    type private ReplayState =
        {
            mutable Revision: int64
            mutable Hash: byte array
            mutable Holds: Map<Guid, DateOnly>
            mutable Phase: string
            mutable CopyEvent: Guid option
            mutable FinalEvent: Guid option
            mutable Policy: string option
            mutable Horizon: DateTimeOffset option
        }

    let private mutation (row: TombstoneHoldAuditRow) =
        match row.Kind, row.ReviewOn with
        | "RECORD", Some reviewOn -> TombstoneHoldMutation.Record(row.HoldId, row.Code, reviewOn)
        | "RELEASE", None -> TombstoneHoldMutation.Release(row.HoldId, row.Code)
        | _ -> corrupt ()

    let private advanceHolds active (row: TombstoneHoldAuditRow) =
        match mutation row with
        | TombstoneHoldMutation.Record(holdId, code, reviewOn) ->
            if
                TombstoneHoldPolicy.validateRecord holdId code reviewOn row.ObservedAt <> Ok()
                || Map.containsKey holdId active
                || active.Count >= 256
            then
                corrupt ()

            Map.add holdId reviewOn active
        | TombstoneHoldMutation.Release(holdId, code) ->
            if
                TombstoneHoldPolicy.validateRelease holdId code <> Ok()
                || not (Map.containsKey holdId active)
            then
                corrupt ()

            Map.remove holdId active

    let private requireWitness
        (witness: WitnessProtocol)
        prunedCutoff
        caseId
        (row: TombstoneHoldAuditRow)
        ct
        =
        task {
            if
                not (
                    prunedCutoff |> Option.exists (fun sealedAt -> row.WitnessSequence <= sealedAt)
                )
            then
                do!
                    witness.VerifyAuthorityEvidenceForCase(
                        row.EventId,
                        row.WitnessSequence,
                        row.WitnessEpoch,
                        row.WitnessHash,
                        row.CandidateHash,
                        caseId,
                        ct
                    )
        }

    let private checkHoldRow
        (witness: WitnessProtocol)
        cutoff
        prunedCutoff
        caseId
        revision
        (previousHash: byte array)
        (row: TombstoneHoldAuditRow)
        ct
        =
        task {
            if
                row.Revision <> revision + 1L
                || row.PreviousHash <> previousHash
                || row.WitnessSequence > cutoff
                || row.WitnessEpoch <> witness.Identity.Epoch
                || row.ActorId = Guid.Empty
                || row.GrantRevision < 1L
            then
                corrupt ()

            let change =
                {
                    EventId = row.EventId
                    CaseId = caseId
                    ExpectedAuthorityRevision = revision
                    ExpectedAuthorityHash = Convert.ToHexStringLower previousHash
                    Mutation = mutation row
                }

            let canonical =
                CaseTombstoneCandidate.hold
                    change
                    row.ActorId
                    row.GrantRevision
                    previousHash
                    row.ObservedAt

            try
                if
                    canonical <> row.Canonical
                    || row.CandidateHash <> SHA256.HashData(canonical)
                    || row.EventHash <> CaseTombstoneCandidate.eventHash previousHash canonical
                then
                    corrupt ()

                do! requireWitness witness prunedCutoff caseId row ct
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private checkRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        prunedCutoff
        caseId
        revision
        previousHash
        previousPhase
        (active: Map<Guid, DateOnly>)
        (row: TombstoneHoldAuditRow)
        ct
        =
        task {
            if
                row.Revision <> revision + 1L
                || row.PreviousHash <> previousHash
                || row.WitnessSequence > cutoff
                || row.WitnessEpoch <> witness.Identity.Epoch
            then
                corrupt ()

            if row.Kind = "TERMINAL" then
                let! next =
                    CaseTombstoneTerminalEventAudit.verify
                        connection
                        transaction
                        witness
                        cutoff
                        row.EventId
                        revision
                        previousHash
                        previousPhase
                        (not active.IsEmpty)
                        ct

                return Some next
            else
                do! checkHoldRow witness cutoff prunedCutoff caseId revision previousHash row ct
                return None
        }

    let private foldRow
        connection
        transaction
        witness
        cutoff
        prunedCutoff
        caseId
        (state: ReplayState)
        row
        ct
        =
        task {
            let! terminal =
                checkRow
                    connection
                    transaction
                    witness
                    cutoff
                    prunedCutoff
                    caseId
                    state.Revision
                    state.Hash
                    state.Phase
                    state.Holds
                    row
                    ct

            match terminal with
            | None -> state.Holds <- advanceHolds state.Holds row
            | Some(next, eventId, policyId, until) ->
                state.Phase <- next
                state.Policy <- Some policyId
                state.Horizon <- Some until

                if next = "PAYLOAD_ERASED_SUPPRESSION_RETAINED" then
                    state.CopyEvent <- Some eventId
                else
                    state.FinalEvent <- Some eventId

            state.Revision <- row.Revision
            state.Hash <- row.EventHash
        }

    let private initialState () =
        {
            Revision = 0L
            Hash = Array.zeroCreate<byte> 32
            Holds = Map.empty
            Phase = "ERASURE_PENDING"
            CopyEvent = None
            FinalEvent = None
            Policy = None
            Horizon = None
        }


    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        prunedCutoff
        caseId
        ct
        =
        task {
            let state = initialState ()

            let mutable more = true

            while more do
                let! rows =
                    CaseTombstoneHoldAuditRows.page connection transaction caseId state.Revision

                for row in rows do
                    do!
                        foldRow
                            connection
                            transaction
                            witness
                            cutoff
                            prunedCutoff
                            caseId
                            state
                            row
                            ct

                more <- rows.Length = 50

            let! storedRevision, storedHash =
                CaseTombstoneHoldAuditRows.readTip connection transaction caseId

            if storedRevision <> state.Revision || storedHash <> state.Hash then
                corrupt ()

            let! storedHolds = CaseTombstoneRead.activeHolds connection transaction caseId

            if (storedHolds |> Map.ofList) <> state.Holds then
                corrupt ()

            let! projected =
                CaseTombstoneTerminalProjectionAudit.read connection transaction caseId

            if
                projected
                <> (state.Phase, state.CopyEvent, state.FinalEvent, state.Policy, state.Horizon)
            then
                corrupt ()

            return state.Revision
        }
