namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ActorGrantGateQueries
open ActorGrantGateBinding

module internal ActorGrantTombstoneGate =
    let private permitsTombstone =
        function
        | EndpointAction.ReviewTombstone
        | EndpointAction.ManageTombstoneHold
        | EndpointAction.ApproveWitnessPrune
        | EndpointAction.ApproveTerminalErasure
        | EndpointAction.ApproveCopyAdoption -> true
        | _ -> false

    let admit
        dataSource
        commitments
        principal
        action
        caseId
        (cancellationToken: CancellationToken)
        =
        task {
            let! connection, transaction, revision = openSnapshot dataSource cancellationToken

            use _connection = connection
            use _transaction = transaction

            use query =
                new NpgsqlCommand(
                    "SELECT case_id FROM claimcore.case_erasure_tombstones "
                    + "WHERE case_id=@case AND phase IN "
                    + "('ERASURE_PENDING','PAYLOAD_ERASED_SUPPRESSION_RETAINED',"
                    + "'ERASURE_FINAL') "
                    + "AND purge_event_id IS NOT NULL AND live_purged_at IS NOT NULL",
                    connection,
                    transaction
                )

            Sql.uuid query "case" caseId
            let! located = query.ExecuteScalarAsync(cancellationToken)

            let resolved =
                match located with
                | :? Guid as value when value = caseId && caseId <> Guid.Empty -> Some value
                | _ -> None

            let resource =
                if permitsTombstone action then
                    resolved |> Option.map ResourceScope.Case
                else
                    None

            let! actor =
                authorize
                    connection
                    transaction
                    revision
                    principal
                    action
                    resource
                    cancellationToken

            return context commitments actor resolved action
        }
