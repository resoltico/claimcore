namespace ClaimCore.Postgres

open System
open Npgsql
open DataAuditCommon

module internal CaseTombstoneTerminalProjectionAudit =
    let read connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT phase,copy_absence_event_id,suppression_final_event_id,"
                    + "retention_policy_id,suppression_until "
                    + "FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                corrupt ()

            let value =
                reader.GetString(0),
                (if reader.IsDBNull(1) then None else Some(reader.GetGuid(1))),
                (if reader.IsDBNull(2) then None else Some(reader.GetGuid(2))),
                (if reader.IsDBNull(3) then
                     None
                 else
                     Some(reader.GetString(3))),
                (if reader.IsDBNull(4) then
                     None
                 else
                     Some(reader.GetFieldValue<DateTimeOffset>(4)))

            if reader.Read() then
                corrupt ()

            return value
        }
