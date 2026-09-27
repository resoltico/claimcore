namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Application

module internal DataAuditErasureFences =
    let private terminalApprovals connection transaction (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.case_erasure_terminal_approvals",
                    connection,
                    transaction
                )

            let! value = command.ExecuteScalarAsync(ct)
            return value :?> int64
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (commitments: ISuppressionCommitments option)
        (ct: CancellationToken)
        =
        task {
            let! requested =
                DataAuditErasure.verify connection transaction witness cutoff commitments ct

            let! purged =
                DataAuditPurgedErasure.verify connection transaction witness cutoff commitments ct

            let! approvals = terminalApprovals connection transaction ct
            let! events = DataAuditTerminalEvents.verify connection transaction ct

            return requested + purged, approvals, events
        }
