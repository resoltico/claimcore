namespace ClaimCore.Postgres

open Npgsql
open DataAuditCommon

/// No FK to the live claimant row can survive authorized purge. The full audit instead
/// proves a current active case or the exact later witnessed erasure-request tombstone.
module internal ManagedCopyExternalPublicationCaseLink =
    let verify connection transaction (row: StoredExternalCopyPublication) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT request_witness_sequence,source_revision "
                    + "FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid command "case" row.CaseId
            use! reader = command.ExecuteReaderAsync()

            if reader.Read() then
                if
                    reader.GetInt64(0) <= row.WitnessSequence
                    || reader.GetInt64(1) < row.CaseRevision
                    || reader.Read()
                then
                    corrupt ()
            else
                reader.Close()

                use active =
                    new NpgsqlCommand(
                        "SELECT revision,privacy_phase FROM claimcore.cases WHERE case_id=@case",
                        connection,
                        transaction
                    )

                Sql.uuid active "case" row.CaseId
                use! caseReader = active.ExecuteReaderAsync()

                if
                    not (caseReader.Read())
                    || caseReader.GetInt64(0) < row.CaseRevision
                    || caseReader.GetString(1) <> "ACTIVE"
                    || caseReader.Read()
                then
                    corrupt ()
        }
