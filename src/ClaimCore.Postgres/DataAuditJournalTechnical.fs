namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon
open WitnessTechnicalCandidateRead

module internal DataAuditJournalTechnical =
    let private verifyTerminal
        (command: NpgsqlCommand)
        (candidate: TechnicalCandidateMetadata)
        (ct: CancellationToken)
        =
        task {
            command.Parameters["operation"].Value <- candidate.OperationId
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let! found = reader.ReadAsync(ct)

            if
                not found
                || reader.GetGuid(0) <> candidate.CaseId
                || reader.GetString(1) <> candidate.RequestSha256
                || reader.Read()
            then
                corrupt ()
        }

    let verify
        (witness: WitnessProtocol)
        (technical: NpgsqlCommand)
        (terminal: NpgsqlCommand)
        (ticket: Ticket)
        (ct: CancellationToken)
        =
        task {
            let eventId = ticket.OperationId
            let candidate = witnessProof (fun () -> witness.ReadTechnicalCandidate(eventId))

            if
                candidate.WitnessEventId <> eventId
                || ticket.ScopeKind <> Case
                || ticket.SubjectCaseId <> Some candidate.CaseId
            then
                corrupt ()

            technical.Parameters["operation"].Value <- eventId
            let! found = technical.ExecuteScalarAsync(ct)

            if not (unbox<bool> found) then
                do! verifyTerminal terminal candidate ct
        }
