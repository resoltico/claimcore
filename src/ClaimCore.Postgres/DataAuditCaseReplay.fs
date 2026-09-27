namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain
open ClaimCore.Witness

module internal DataAuditCaseReplay =
    let private readCurrentPage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        after
        (ct: CancellationToken)
        =
        task {
            use page = new NpgsqlCommand(Sql.listCases, connection, transaction)
            Sql.optional page "after" NpgsqlDbType.Text after
            use! reader = page.ExecuteReaderAsync(ct)
            let current = ResizeArray<Guid * Claim>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    current.Add(reader.GetGuid(reader.GetOrdinal("case_id")), Rows.claim reader)

            return List.ofSeq current
        }

    let replayCases
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        zone
        (witness: WitnessProtocol)
        cutoff
        (ct: CancellationToken)
        =
        task {
            let mutable after: string option = None
            let mutable more = true
            let mutable cases = 0L
            let mutable operations = 0L
            let mutable lifecycleEvents = 0L

            while more do
                let! current = readCurrentPage connection transaction after ct

                for caseId, claim in current do
                    let! lifecycle =
                        CaseLifecycleAudit.verifyCase
                            connection
                            transaction
                            witness
                            cutoff
                            caseId
                            claim
                            ct

                    let! accepted =
                        DataAuditReplay.replayCase
                            connection
                            transaction
                            zone
                            witness
                            cutoff
                            caseId
                            claim
                            lifecycle.DispositionCount
                            lifecycle.LastBusinessSnapshot
                            ct

                    cases <- cases + 1L
                    operations <- operations + accepted
                    lifecycleEvents <- lifecycleEvents + lifecycle.Count

                match current |> Seq.tryLast with
                | None -> more <- false
                | Some(_, last) ->
                    after <- Some((Claim.view last).Fields.CaseReference)

                    more <-
                        current.Length >
                            ClaimCore.Application.SemanticContract.current.MaximumPageSize

            return cases, operations, lifecycleEvents
        }
