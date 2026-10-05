namespace ClaimCore.Postgres

open System.Threading
open Npgsql

/// Historical payment assertions cannot disappear from a void-approval decision after
/// CLEAR_PAYMENT. The query runs under the same case and authority locks as the transition.
module internal CaseLifecyclePaymentEvidence =
    let historicalPayment connection transaction caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.case_changes "
                    + "WHERE case_id=@caseId AND (convert_from(snapshot,'UTF8')::jsonb "
                    + "-> 'fields' ->> 'paymentDate') IS NOT NULL)",
                    connection,
                    transaction
                )

            Sql.uuid command "caseId" caseId
            let! result = command.ExecuteScalarAsync(ct)
            return result :?> bool
        }
