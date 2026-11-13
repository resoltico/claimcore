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

    let cases connection installationId (state: Replay.Scan) token =
        task {
            let expected = Replay.caseRecords state

            let basic (record: StoredCase) =
                record.CaseId, record.Snapshot, record.SnapshotBytes

            let bases = expected |> Result.map (Map.map (fun _ record -> basic record))

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

                                    let! expectedRows = expected

                                    let! expectedRow =
                                        Map.tryFind record.CaseId expectedRows
                                        |> known None VerificationFinding.CaseProjection

                                    do!
                                        ProjectionVerification.column
                                            VerificationFinding.CaseProjection
                                            basic
                                            expectedRow
                                            record

                                    let column finding get =
                                        ProjectionVerification.column finding get expectedRow record

                                    do!
                                        column VerificationFinding.CaseStatusProjection (fun row ->
                                            row.Index.Status)

                                    do!
                                        column
                                            VerificationFinding.CaseProgressProjection
                                            (fun row -> row.Index.Progress)

                                    do!
                                        column
                                            VerificationFinding.CaseChangedAtProjection
                                            (fun row -> row.Index.ChangedAt)

                                    do!
                                        column VerificationFinding.CaseSearchProjection (fun row ->
                                            row.Index.SearchText)

                                    do!
                                        column
                                            VerificationFinding.CaseNotifiedOnProjection
                                            (fun row -> row.Index.NotifiedOn)

                                    do!
                                        column
                                            VerificationFinding.CaseClaimedAmountProjection
                                            (fun row -> row.Index.Claimed |> Option.map Money.value)

                                    do!
                                        column
                                            VerificationFinding.CaseClaimedCurrencyProjection
                                            (fun row ->
                                                row.Index.Claimed |> Option.map Money.currency)

                                    do!
                                        column
                                            VerificationFinding.CasePayableAmountProjection
                                            (fun row -> row.Index.Payable |> Option.map Money.value)

                                    do!
                                        column
                                            VerificationFinding.CasePayableCurrencyProjection
                                            (fun row ->
                                                row.Index.Payable |> Option.map Money.currency)

                                    do!
                                        column
                                            VerificationFinding.CasePaidAmountProjection
                                            (fun row -> row.Index.Paid |> Option.map Money.value)

                                    let! bases = bases

                                    let! seen =
                                        ProjectionVerification.row
                                            VerificationFinding.CaseProjection
                                            (fun (id, _, _) -> id)
                                            bases
                                            seen
                                            (basic record)

                                    return
                                        seen,
                                        (record.CaseId,
                                         record.Index.Responsible,
                                         record.Index.FollowUp,
                                         record.Index.WorkSearchText)
                                        :: workIndexes

                                }
                        Initial = Set.empty, []
                        Finding = VerificationFinding.CaseProjection
                    }
                    token

            return
                checkedCases
                |> Result.bind (fun (seen, workIndexes) ->
                    expected
                    |> Result.bind (fun expected ->
                        ProjectionVerification.finishRows
                            VerificationFinding.CaseProjection
                            expected
                            seen)
                    |> Result.map (fun () -> List.rev workIndexes))
        }

    let private compareRows connection finding key expected sql read token =
        task {
            let scan =
                {
                    Read = fun reader () -> DatabaseSession.row reader |> read
                    Append = ProjectionVerification.row finding key expected
                    Initial = Set.empty
                    Finding = finding
                }

            let! seen = fold connection sql scan token
            return seen |> Result.bind (ProjectionVerification.finishRows finding expected)
        }

    let people connection (state: Replay.Scan) token =
        task {
            match Replay.peopleRows state with
            | Error finding -> return Error finding
            | Ok expected ->
                let identities =
                    expected |> Map.map (fun _ row -> { row.Actor with Profile = None })

                let! actors =
                    compareRows
                        connection
                        VerificationFinding.PeopleProjection
                        (fun (actor: Actor) -> actor.ActorId)
                        identities
                        "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors ORDER BY actor_id"
                        (fun row ->
                            { Projections.actorRow row with
                                Profile = None
                            })
                        token

                match actors with
                | Error finding -> return Error finding
                | Ok() ->
                    let grants =
                        expected
                        |> Map.toSeq
                        |> Seq.collect (fun (id, row) ->
                            row.Grants
                            |> Map.toSeq
                            |> Seq.map (fun (grant, position) ->
                                (id, grant), ((id, grant), position)))
                        |> Map.ofSeq

                    let! checkedGrants =
                        compareRows
                            connection
                            VerificationFinding.PeopleProjection
                            fst
                            grants
                            "SELECT actor_id,role,case_id,granted_sequence,granted_effect_index FROM claimcore.grants ORDER BY actor_id,role,case_id NULLS FIRST"
                            (fun row ->
                                let id, role, caseId = grant row

                                let scope =
                                    caseId
                                    |> Option.map Scope.Case
                                    |> Option.defaultValue Scope.Installation

                                let sequence =
                                    DatabaseSession.value<int64> 3 row
                                    |> DatabaseSession.admitted Sequence.create

                                let position =
                                    DatabaseSession.value<int> 4 row
                                    |> DatabaseSession.admitted (EffectPosition.create sequence)

                                (id,
                                 {
                                     ActorGrant.Role = role
                                     Scope = scope
                                 }),
                                position)
                            token

                    match checkedGrants with
                    | Error finding -> return Error finding
                    | Ok() ->
                        let profiles =
                            expected
                            |> Map.map (fun id row ->
                                id, row.Actor.Profile, row.DisplayNameSearch, row.ContactSearch)

                        return!
                            compareRows
                                connection
                                VerificationFinding.PeopleProfileProjection
                                (fun (id, _, _, _) -> id)
                                profiles
                                "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact,display_name_search,contact_search FROM claimcore.actors ORDER BY actor_id"
                                (fun row ->
                                    let actor = Projections.actorRow row

                                    actor.ActorId,
                                    actor.Profile,
                                    DatabaseSession.optional<string> 7 row,
                                    DatabaseSession.optional<string> 8 row)
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

                    let! expectedCases = Replay.caseRows state

                    let expectedIndexes =
                        expectedCases
                        |> Map.map (fun id (_, index) ->
                            id, index.Responsible, index.FollowUp, index.WorkSearchText)

                    return!
                        ProjectionVerification.rows
                            VerificationFinding.CaseWorkProjection
                            (fun (id, _, _, _) -> id)
                            expectedIndexes
                            workIndexes
                }
        }
