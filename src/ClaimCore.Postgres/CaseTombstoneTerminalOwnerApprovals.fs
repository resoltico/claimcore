namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application
open OwnerTerminalDecisionData

[<NoEquality; NoComparison>]
type private OwnerTerminalApprovalRow =
    {
        Evidence: ApprovalEvidence
        WitnessEpoch: int64
        ApprovedAt: DateTimeOffset
    }

/// Current authorization is re-read under the owner authority lock. Historical witness proof
/// remains immutable, but a revoked steward cannot authorize an irreversible transition now.
module internal CaseTombstoneTerminalOwnerApprovals =
    let private invalid () =
        raise (InvalidDataException("Terminal owner approvals are incomplete."))

    let private rows connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id,approver_actor_id,approver_grant_revision,"
                    + "witness_sequence,witness_entry_hash,approval_witness_epoch,"
                    + "approved_at,expires_at FROM claimcore.case_erasure_terminal_approvals "
                    + "WHERE terminal_event_id=@event ORDER BY approver_actor_id LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()
            let values = ResizeArray<OwnerTerminalApprovalRow>()

            while reader.Read() do
                values.Add
                    {
                        Evidence =
                            {
                                ApprovalId = reader.GetGuid(0)
                                ActorId = reader.GetGuid(1)
                                GrantRevision = reader.GetInt64(2)
                                WitnessSequence = reader.GetInt64(3)
                                WitnessHash =
                                    Convert.ToHexStringLower(reader.GetFieldValue<byte array>(4))
                                ExpiresAt = reader.GetFieldValue<DateTimeOffset>(7)
                            }
                        WitnessEpoch = reader.GetInt64(5)
                        ApprovedAt = reader.GetFieldValue<DateTimeOffset>(6)
                    }

            return values |> Seq.toList
        }

    let private principal connection transaction actorId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT principal_kind,issuer,principal_value FROM claimcore.actors "
                    + "WHERE actor_id=@actor",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actorId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) || reader.GetString(0) <> "HUMAN" then
                invalid ()

            let value =
                PrincipalKey.fromStorage
                    (reader.GetString(0))
                    (reader.GetString(1))
                    (reader.GetString(2))
                |> Result.defaultWith (fun _ -> invalid ())

            if reader.Read() then
                invalid ()

            return value
        }

    let private currentGrant
        connection
        transaction
        revision
        caseId
        (row: OwnerTerminalApprovalRow)
        =
        task {
            let! key = principal connection transaction row.Evidence.ActorId

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    key
                    (ResourceScope.Case caseId)
                    revision
                    CancellationToken.None

            match authority with
            | None -> invalid ()
            | Some actor ->
                match
                    ActorAuthorization.authorizeAtRevision
                        key
                        actor
                        row.Evidence.GrantRevision
                        EndpointAction.ApproveTerminalErasure
                        (ResourceScope.Case caseId)
                with
                | AuthorizationDecision.Available(actorId, _) when actorId = row.Evidence.ActorId ->
                    ()
                | _ -> invalid ()
        }

    let read connection transaction witness revision proposal observedAt =
        task {
            let value = TombstoneTerminalProposal.copy proposal

            let! same =
                CaseTombstoneTerminalApprovalSet.matches connection transaction witness proposal

            let! approvals = rows connection transaction value.EventId

            if
                not same
                || approvals.Length <> 2
                || approvals[0].Evidence.ActorId = approvals[1].Evidence.ActorId
            then
                invalid ()

            for row in approvals do
                if
                    row.WitnessEpoch <> witness.Identity.Epoch
                    || row.Evidence.ExpiresAt <= observedAt
                    || row.Evidence.ExpiresAt > value.ValidUntil
                    || row.ApprovedAt >= row.Evidence.ExpiresAt
                then
                    invalid ()

                do! currentGrant connection transaction revision value.CaseId row

            return approvals |> List.map _.Evidence
        }
