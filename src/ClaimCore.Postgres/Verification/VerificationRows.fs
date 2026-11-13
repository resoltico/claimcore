namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Application
open ClaimCore.Application.VerificationChecks

[<NoEquality; NoComparison>]
type private ProjectionScan<'value, 'state> =
    {
        Read: Npgsql.NpgsqlDataReader -> (unit -> 'value)
        Append: 'state -> 'value -> Result<'state, VerificationDivergence>
        Initial: 'state
        Finding: VerificationFinding
    }

module internal VerificationRows =
    let capture sequence finding read =
        try
            VerificationChecks.capture sequence finding read
        with :? RecordCorruptException ->
            Error(divergence sequence finding)

    let private grant row =
        let role =
            (Vocabulary.tryParse Role.vocabulary) (DatabaseSession.value<string> 1 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid projected role."))

        (DatabaseSession.value<System.Guid> 0 row
         |> DatabaseSession.admitted ActorId.create),
        role,
        (DatabaseSession.optional<System.Guid> 2 row
         |> Option.map (DatabaseSession.admitted CaseId.create))

    let private fold
        connection
        sql
        (scan: ProjectionScan<'value, 'state>)
        (token: System.Threading.CancellationToken)
        =
        task {
            use statement = DatabaseSession.statement connection sql [||]
            use! reader = statement.ExecuteReaderAsync(token)
            let read = scan.Read reader
            let mutable state = Ok scan.Initial
            let mutable reading = true

            while reading && Result.isOk state do
                let! found = reader.ReadAsync(token)

                if not found then
                    reading <- false
                else
                    state <-
                        state
                        |> Result.bind (fun state ->
                            capture None scan.Finding read |> Result.bind (scan.Append state))

            return state
        }

    let cases connection installationId histories token =
        task {
            let! checkedCases =
                fold
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases c ORDER BY case_id")
                    {
                        Read = DatabaseSession.namedRow
                        Append =
                            fun (seen, workIndexes) row ->
                                result {
                                    let! status =
                                        capture
                                            None
                                            VerificationFinding.CaseStatusProjection
                                            (fun () -> Projections.caseStatus row)

                                    let! progress =
                                        capture
                                            None
                                            VerificationFinding.CaseProgressProjection
                                            (fun () -> Projections.caseProgress row)

                                    let! changedAt =
                                        capture
                                            None
                                            VerificationFinding.CaseChangedAtProjection
                                            (fun () -> Projections.caseChangedAt row)

                                    let! searchText =
                                        capture
                                            None
                                            VerificationFinding.CaseSearchProjection
                                            (fun () -> Projections.caseSearchText row)

                                    let! notifiedOn =
                                        capture
                                            None
                                            VerificationFinding.CaseNotifiedOnProjection
                                            (fun () -> Projections.caseNotifiedOn row)

                                    let! claimedAmount =
                                        capture
                                            None
                                            VerificationFinding.CaseClaimedAmountProjection
                                            (fun () -> Projections.caseClaimedAmount row)

                                    let! claimedCurrency =
                                        capture
                                            None
                                            VerificationFinding.CaseClaimedCurrencyProjection
                                            (fun () -> Projections.caseClaimedCurrency row)

                                    let! payableAmount =
                                        capture
                                            None
                                            VerificationFinding.CasePayableAmountProjection
                                            (fun () -> Projections.casePayableAmount row)

                                    let! paidAmount =
                                        capture
                                            None
                                            VerificationFinding.CasePaidAmountProjection
                                            (fun () -> Projections.casePaidAmount row)

                                    let! payableCurrency =
                                        capture
                                            None
                                            VerificationFinding.CasePayableCurrencyProjection
                                            (fun () -> Projections.casePayableCurrency row)

                                    let! responsible =
                                        capture
                                            None
                                            VerificationFinding.CaseWorkProjection
                                            (fun () -> Projections.caseResponsibleActor row)

                                    let! followUp =
                                        capture
                                            None
                                            VerificationFinding.CaseWorkProjection
                                            (fun () -> Projections.caseFollowUp row)

                                    let! workSearchText =
                                        capture
                                            None
                                            VerificationFinding.CaseWorkProjection
                                            (fun () ->
                                                DatabaseSession.namedOptional<string>
                                                    "work_search_text"
                                                    row)

                                    let index: CaseIndex =
                                        {
                                            Responsible = responsible
                                            FollowUp = followUp
                                            Status = status
                                            Progress = progress
                                            ChangedAt = changedAt
                                            SearchText = searchText
                                            WorkSearchText = workSearchText
                                            NotifiedOn = notifiedOn
                                            Claimed =
                                                Projections.indexedMoney
                                                    claimedAmount
                                                    claimedCurrency
                                            Paid =
                                                Projections.indexedMoney paidAmount payableCurrency
                                            Payable =
                                                Projections.indexedMoney
                                                    payableAmount
                                                    payableCurrency
                                        }

                                    let! record =
                                        capture None VerificationFinding.CaseProjection (fun () ->
                                            Projections.caseRowWithIndex installationId index row)

                                    let! seen =
                                        ProjectionVerification.caseProjection
                                            installationId
                                            histories
                                            seen
                                            record

                                    return
                                        seen,
                                        ProjectionVerification.caseWorkIndex record :: workIndexes
                                }
                        Initial = Set.empty, []
                        Finding = VerificationFinding.CaseProjection
                    }
                    token

            return
                checkedCases
                |> Result.bind (fun (seen, workIndexes) ->
                    ProjectionVerification.finishCases histories seen
                    |> Result.map (fun () -> List.rev workIndexes))
        }

    let people connection expected sources token =
        task {
            let! actors =
                fold
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors ORDER BY actor_id"
                    {
                        Read = fun reader () -> DatabaseSession.row reader |> Projections.actorRow
                        Append = ProjectionVerification.actor
                        Initial = Map.empty
                        Finding = VerificationFinding.PeopleProjection
                    }
                    token

            match actors with
            | Error error -> return Error error
            | Ok actors ->
                let! actual =
                    fold
                        connection
                        "SELECT actor_id,role,case_id,granted_sequence,granted_effect_index FROM claimcore.grants ORDER BY actor_id,role,case_id"
                        {
                            Read =
                                fun reader () ->
                                    let row = DatabaseSession.row reader in

                                    grant row,
                                    (let sequence =
                                        DatabaseSession.value<int64> 3 row
                                        |> DatabaseSession.admitted Sequence.create

                                     EffectPosition.create
                                         sequence
                                         (DatabaseSession.value<int> 4 row)
                                     |> Option.defaultWith (fun () ->
                                         raise (
                                             RecordCorruptException "Invalid grant effect source."
                                         )))
                            Append =
                                fun actors ((id, role, caseId), source) ->
                                    let scope =
                                        caseId
                                        |> Option.map Scope.Case
                                        |> Option.defaultValue Scope.Installation

                                    if Map.tryFind (id, role, scope) sources <> Some source then
                                        Error(divergence None VerificationFinding.PeopleProjection)
                                    else
                                        ProjectionVerification.grant actors (id, role, caseId)
                            Initial = actors
                            Finding = VerificationFinding.PeopleProjection
                        }
                        token

                match actual |> Result.bind (ProjectionVerification.finishPeople expected) with
                | Error error -> return Error error
                | Ok() ->
                    return!
                        fold
                            connection
                            "SELECT actor_id,display_name_search,contact_search FROM claimcore.actors ORDER BY actor_id"
                            {
                                Read =
                                    (fun reader () ->
                                        let row = DatabaseSession.row reader in

                                        DatabaseSession.value<System.Guid> 0 row
                                        |> DatabaseSession.admitted ActorId.create,
                                        DatabaseSession.optional<string> 1 row,
                                        DatabaseSession.optional<string> 2 row)
                                Append =
                                    (fun () row ->
                                        ProjectionVerification.actorProfileSearch expected row)
                                Initial = ()
                                Finding = VerificationFinding.PeopleProfileProjection
                            }
                            token
        }

    let family connection (state: Replay.Scan) workIndexes token =
        task {
            let! work =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {WorkStorage.workColumns} FROM claimcore.case_work")
                    [||]
                    token

            let! reminders =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {WorkStorage.reminderColumns} FROM claimcore.reminders r")
                    [||]
                    token

            let! notes =
                DatabaseSession.namedRows
                    connection
                    ("SELECT " + NoteStorage.columns + NoteStorage.source)
                    [||]
                    token

            return
                result {
                    let! work =
                        capture None VerificationFinding.CaseWorkProjection (fun () ->
                            work |> List.map WorkStorage.workProjectionRow)

                    let! reminders =
                        capture None VerificationFinding.CaseWorkProjection (fun () ->
                            reminders |> List.map WorkStorage.reminderProjectionRow)

                    let! notes =
                        capture None VerificationFinding.CaseNotesProjection (fun () ->
                            notes |> List.map NoteStorage.projectionRow)

                    let! expectedWork = Replay.workRows state
                    let! expectedReminders = Replay.reminderRows state
                    let! expectedNotes = Replay.noteRows state

                    do!
                        ProjectionVerification.rows
                            VerificationFinding.CaseNotesProjection
                            (fun (row: NoteSubjectRows) -> row.NoteId)
                            expectedNotes
                            notes

                    do!
                        ProjectionVerification.rows
                            VerificationFinding.CaseWorkProjection
                            (fun (row: WorkSubjectRows) -> row.CaseId)
                            expectedWork
                            work

                    do!
                        ProjectionVerification.rows
                            VerificationFinding.CaseWorkProjection
                            (fun (row: ReminderSubjectRows) -> row.ReminderId)
                            expectedReminders
                            reminders

                    return!
                        ProjectionVerification.workIndexes state.Work state.Reminders workIndexes
                }
        }
