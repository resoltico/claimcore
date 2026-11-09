namespace ClaimCore.Postgres

open ClaimCore.RecordFormat
open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open Npgsql

module internal TransactionPages =
    let grantPageSql =
        let grantedAt = DatabaseSession.instantExpression "p.recorded_at"
        ($"SELECT g.role,c.case_reference,g.case_id,l.creation_reference,{grantedAt},p.actor_id FROM claimcore.grants g JOIN claimcore.ledger p ON p.sequence=g.granted_sequence LEFT JOIN claimcore.cases c USING(case_id) LEFT JOIN claimcore.ledger l ON l.subject_kind='CASE' AND l.subject_id=g.case_id AND l.case_revision=1 AND l.act='OPEN' WHERE g.actor_id=$1 AND ($2::text IS NULL OR g.role>$2 OR (g.role=$2 AND $3::uuid IS NOT NULL AND (g.case_id>$3 OR g.case_id IS NULL))) ORDER BY g.role ASC,g.case_id ASC NULLS LAST LIMIT $4")

    let page limit position rows =
        let visible = List.truncate limit rows

        {
            Items = visible
            Next =
                if List.length rows > limit then
                    List.tryLast visible |> Option.map position
                else
                    None
        }

    let private caseQuery (query: CasePageQuery) =
        let key = "upper(c.case_reference COLLATE \"C\")"

        let after, sorting, values, limit =
            match query.Filter.Order, query.AfterPosition with
            | CaseListOrder.Reference, position ->
                let reference =
                    match position with
                    | None -> None
                    | Some(CaseListPosition.Reference value) -> Some value
                    | _ -> invalidArg (nameof query) "Expected a reference position."

                key + " > $13 COLLATE \"C\"",
                key + " ASC",
                [| DatabaseSession.parameter reference |],
                "$14"
            | CaseListOrder.Recent, position ->
                let reference, changedAt =
                    match position with
                    | None -> None, None
                    | Some(CaseListPosition.Recent(time, reference)) -> Some reference, Some time
                    | _ -> invalidArg (nameof query) "Expected a recent position."

                "(c.changed_at < $14::timestamptz OR (c.changed_at = $14 AND "
                + key
                + " > $13 COLLATE \"C\"))",
                "c.changed_at DESC," + key + " ASC",
                [| DatabaseSession.parameter reference; DatabaseSession.parameter changedAt |],
                "$15"
            | CaseListOrder.Notified, position ->
                let reference, date =
                    match position with
                    | None -> None, None
                    | Some(CaseListPosition.Notified(date, reference)) -> Some reference, date
                    | _ -> invalidArg (nameof query) "Expected a notification position."

                "(($14::date IS NULL AND c.notified_on IS NULL AND "
                + key
                + " > $13 COLLATE \"C\") OR ($14::date IS NOT NULL AND (c.notified_on > $14::date OR c.notified_on IS NULL OR (c.notified_on=$14::date AND "
                + key
                + " > $13 COLLATE \"C\"))))",
                "c.notified_on ASC NULLS LAST," + key + " ASC",
                [|
                    DatabaseSession.parameter reference
                    DatabaseSession.parameter (date |> Option.map CalendarDate.format)
                |],
                "$15"

            | CaseListOrder.FollowUp, position ->
                let reference, date =
                    match position with
                    | None -> None, None
                    | Some(CaseListPosition.FollowUp(date, reference)) -> Some reference, date
                    | _ -> invalidArg (nameof query) "Expected a follow-up position."

                "(($14::date IS NULL AND c.follow_up IS NULL AND "
                + key
                + " > $13 COLLATE \"C\") OR ($14::date IS NOT NULL AND (c.follow_up > $14::date OR c.follow_up IS NULL OR (c.follow_up=$14::date AND "
                + key
                + " > $13 COLLATE \"C\"))))",
                "c.follow_up ASC NULLS LAST," + key + " ASC",
                [|
                    DatabaseSession.parameter reference
                    DatabaseSession.parameter (date |> Option.map CalendarDate.format)
                |],
                "$15"

        let sql =
            $"SELECT {Projections.caseColumns} FROM claimcore.cases c WHERE {CaseSelectionSql.predicate query.Selection} AND ($13::text IS NULL OR {after}) ORDER BY {sorting} LIMIT {limit}"

        let parameters =
            Array.concat
                [
                    CaseSelectionSql.parameters query.Selection
                    values
                    [| box (query.Limit + 1) |]
                ]

        sql, parameters

    let cases connection installation (query: CasePageQuery) token =
        task {
            let sql, parameters = caseQuery query
            let! rows = DatabaseSession.namedRows connection sql parameters token

            let selected =
                rows
                |> List.map (Projections.caseRow installation)
                |> page query.Limit (fun record ->
                    CaseListPosition.create
                        query.Filter.Order
                        record.Index.ChangedAt
                        record.Index.NotifiedOn
                        record.Index.FollowUp
                        (CaseReference.format record.Snapshot.Reference))
            // Load only visible parents' independently owned work; the lookahead remains financial-only.
            let! work =
                WorkStorage.summaries
                    connection
                    (selected.Items |> List.map _.CaseId)
                    query.Selection.DateBasis.BusinessDate
                    token

            return
                {
                    Items =
                        selected.Items
                        |> List.map (fun record ->
                            {
                                CasePageItem.Case = record
                                WorkSummary = Map.tryFind record.CaseId work
                            })
                    Next = selected.Next
                }
        }

    let authorityHistory connection installation (query: AuthorityHistoryPageQuery) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE subject_kind='AUTHORITY' AND ($1::uuid IS NULL OR subject_id=$1) AND ($2::bigint IS NULL OR sequence<$2) ORDER BY sequence DESC LIMIT $3")
                    [|
                        DatabaseSession.parameter (query.TargetActorId |> Option.map ActorId.value)
                        DatabaseSession.parameter (
                            query.BeforeSequence |> Option.map Sequence.value
                        )
                        box (query.Limit + 1)
                    |]
                    token

            return
                rows
                |> List.map (StoredEntries.decode installation >> _.Entry)
                |> page query.Limit (fun entry -> entry.Sequence)
        }

    let actors connection text role after limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors WHERE ($1::uuid IS NULL OR actor_id>$1) AND ($3::text IS NULL OR (display_name_search LIKE '%'||$3||'%' ESCAPE chr(92) OR contact_search LIKE '%'||$3||'%' ESCAPE chr(92))) AND ($4::text IS NULL OR EXISTS(SELECT 1 FROM claimcore.grants assigned WHERE assigned.actor_id=actors.actor_id AND assigned.role=$4)) ORDER BY actor_id LIMIT $2"
                    [|
                        DatabaseSession.parameter (after |> Option.map ActorId.value)
                        box (limit + 1)
                        DatabaseSession.parameter (
                            text
                            |> Option.map (fun query ->
                                DirectorySearchText.value query
                                |> fun value ->
                                    value
                                        .Replace("\\", "\\\\")
                                        .Replace("%", "\\%")
                                        .Replace("_", "\\_"))
                        )
                        DatabaseSession.parameter (role |> Option.map Role.token)
                    |]
                    token

            return rows |> List.map Projections.actorRow |> page limit (fun actor -> actor.ActorId)
        }

    let grants connection target (after: GrantPosition option) limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    grantPageSql
                    [|
                        box (ActorId.value target)
                        DatabaseSession.parameter (
                            after |> Option.map (fun value -> Role.token value.Role)
                        )
                        DatabaseSession.parameter (
                            after |> Option.bind _.CaseId |> Option.map CaseId.value
                        )
                        box (limit + 1)
                    |]
                    token

            let visible = rows |> List.map Projections.grantRow |> page limit snd

            return
                {
                    Items = visible.Items |> List.map fst
                    Next = visible.Next
                }
        }

/// An instance is scoped to one provider transaction; it contains no operation callbacks.
type private ReadTx
    (connection: NpgsqlConnection, installation, observedAt, token: CancellationToken) =
    interface IReadTx with
        member _.ObservedAt = observedAt

        member _.Maintenance() =
            RegisterMaintenanceStore.read connection token

        member _.GlobalReadFacts principal =
            Projections.globalReadFacts connection principal token


        member _.Authority
            ({
                 AuthorityQuery.Principal = principal
                 CaseId = caseId
             })
            =
            match caseId with
            | Some id -> Projections.actor connection principal [ id ] token
            | None -> Projections.actorWithVisibility connection principal token

        member _.AuthorityCases(principal, ids) =
            Projections.actor connection principal (List.distinct ids) token

        member _.FindCase id =
            Projections.caseById connection installation id token

        member _.FindCaseByReference reference =
            Projections.caseByReference connection installation reference token

        member _.ActivePayment(parent, identity) =
            PaymentStorage.active connection (parent, identity) token

        member _.LatestPayments(parent, limit) =
            PaymentStorage.latest connection (parent, limit) token

        member _.PaymentBoundsExcluding(parent, selected) =
            PaymentStorage.remainingDates connection (parent, selected) token

        member _.FindWork id =
            WorkStorage.findWork connection id token

        member _.FindNote id = NoteStorage.find connection id token

        member _.NotePage query = NoteStorage.page connection query token

        member _.FindReminder id =
            WorkStorage.findReminder connection id token

        member _.WorkSummaries(ids, today) =
            WorkStorage.summaries connection ids today token

        member _.AssignedAccounts pairs =
            WorkStorage.accounts connection pairs token

        member _.ResponsibilityCounts(selection, actors) =
            ResponsibilityCountStorage.counts connection selection actors token

        member _.ReminderPage query = WorkStorage.page connection query token

        member _.CaseViewCounts selection =
            ViewCountStorage.cases connection selection token

        member _.ReminderViewCounts selection =
            ViewCountStorage.reminders connection selection token

        member _.CaseStageFacts caseId =
            TimelineStorage.stages connection installation caseId token

        member _.TimelinePage query =
            TimelineStorage.page connection installation query token

        member _.PreviousEntries entries =
            TimelineStorage.previous connection installation entries token

        member _.FindOperation id =
            StoredEntries.operation connection installation id token

        member _.CasePage query =
            TransactionPages.cases connection installation query token

        member _.CaseSummary query =
            CaseSummaryQuery.read connection query token

        member _.AuthorityHistoryPage query =
            TransactionPages.authorityHistory connection installation query token

        member _.AccessRequestPage query =
            AccessRequests.page connection query token

        member _.ActorPage
            ({
                 ActorPageQuery.AfterActorId = after
                 Text = text
                 Role = role
                 Limit = limit
             })
            =
            TransactionPages.actors connection text role after limit token

        member _.GrantPage
            ({
                 GrantPageQuery.ActorId = id
                 AfterGrant = after
                 Limit = limit
             })
            =
            TransactionPages.grants connection id after limit token

        member _.AssigneePage query =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT a.actor_id,a.principal_kind,a.issuer,a.subject,a.enabled,a.display_name,a.contact FROM claimcore.actors a WHERE a.enabled AND a.principal_kind='HUMAN' AND ($4::uuid IS NULL OR a.actor_id>$4) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=$1) AND g.role=ANY($2::text[])) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=$1) AND g.role=ANY($3::text[])) ORDER BY a.actor_id LIMIT $5"
                        [|
                            box (CaseId.value query.CaseId)
                            box (query.ReadRoles |> List.map Role.token |> List.toArray)
                            box (query.WorkRoles |> List.map Role.token |> List.toArray)
                            DatabaseSession.parameter (
                                query.AfterActorId |> Option.map ActorId.value
                            )
                            box (query.Limit + 1)
                        |]
                        token

                return
                    rows
                    |> List.map (Projections.actorRow >> Actor.currentReference)
                    |> TransactionPages.page query.Limit _.ActorId
            }

        member _.ActorRoleSummaries ids =
            Projections.actorRoleSummaries connection ids token

        member _.ActorRefs ids =
            task {
                if ids.IsEmpty then
                    return Map.empty
                else
                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors WHERE actor_id=ANY($1::uuid[])"
                            [|
                                box (ids |> List.distinct |> List.map ActorId.value |> List.toArray)
                            |]
                            token

                    return
                        rows
                        |> List.map (fun row ->
                            let actor = Projections.actorRow row in
                            actor.ActorId, Actor.currentReference actor)
                        |> Map.ofList
            }

        member _.FindActor id =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors WHERE actor_id=$1"
                        [| box (ActorId.value id) |]
                        token

                return rows |> List.tryExactlyOne |> Option.map Projections.actorRow
            }

        member _.EnabledHumanOwners() =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role=$1 AND g.case_id IS NULL"
                        [| box (Role.token Role.Owner) |]
                        token

                return DatabaseSession.value<int> 0 rows.Head
            }

        member _.ActorCount() =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*)::int FROM claimcore.actors"
                        [||]
                        token

                return DatabaseSession.value<int> 0 rows.Head
            }

/// Construction is private and occurs only after lock_ledger_head returns in IStore.Write.
type private WriteTx(connection, installation, head, observedAt, token) =
    inherit ReadTx(connection, installation, observedAt, token)

    interface IWriteTx with
        member _.Head = head

        member _.NewPayloadNonce() =
            System.Security.Cryptography.RandomNumberGenerator.GetBytes 32

        member _.Append entry =
            task {
                FailureDiagnostics.phase FailurePhase.Append
                let started = System.Diagnostics.Stopwatch.GetTimestamp()

                try
                    let! _ = Persistence.entry connection entry token
                    return ()
                finally
                    Service.recordAppend (System.Diagnostics.Stopwatch.GetElapsedTime started)
            }

        member _.ProjectCase projection =
            FailureDiagnostics.phase FailurePhase.Projection
            Persistence.case connection projection token

        member _.ProjectWorkChange projection =
            FailureDiagnostics.phase FailurePhase.Projection
            Persistence.workChange connection projection token

        member _.DismissAccessRequest principal =
            task {
                let! _ = AccessRequests.lockPrincipal connection principal token
                let! _ = AccessRequests.delete connection principal token
                return ()
            }

        member _.ProjectAuthority projection =
            FailureDiagnostics.phase FailurePhase.Projection
            Persistence.authority connection projection token

type internal LedgerStore(source: NpgsqlDataSource, installationId, settings) =
    static member InTransaction(connection, installationId, head, observedAt, token) =
        WriteTx(connection, installationId, head, observedAt, token) :> IWriteTx

    interface IStore with
        member _.Context query =
            Transactions.write source settings query.CancellationToken (fun connection ->
                task {
                    // READ COMMITTED is essential: the advisory-lock statement may wait behind registration.
                    // The following single authority query must acquire a fresh snapshot after that wait.
                    let! _ =
                        AccessRequests.lockPrincipal
                            connection
                            query.Principal
                            query.CancellationToken

                    let! observed =
                        DatabaseSession.rows
                            connection
                            "SELECT clock_timestamp()"
                            [||]
                            query.CancellationToken

                    let observedAt = DatabaseSession.instant 0 observed.Head

                    let! facts =
                        Projections.globalReadFacts
                            connection
                            query.Principal
                            query.CancellationToken

                    if facts.Authority.Actor.IsNone then
                        do!
                            AccessRequests.refresh
                                connection
                                query.Principal
                                query.Claims
                                observedAt
                                query.CancellationToken

                    return
                        {
                            ContextFacts.Facts = facts
                            ObservedAt = observedAt
                        },
                        true
                })

        member _.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.read source token (fun connection observedAt ->
                work (ReadTx(connection, installationId, observedAt, token) :> IReadTx))

        member _.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.write source settings token (fun connection ->
                task {
                    let! head, observedAt = Transactions.head connection token
                    FailureDiagnostics.phase FailurePhase.Read

                    let! verdict =
                        work (
                            WriteTx(connection, installationId, head, observedAt, token)
                            :> IWriteTx
                        )

                    let commit =
                        match verdict with
                        | Verdict.Proceed _ -> true
                        | Verdict.Replay _
                        | Verdict.Refuse _
                        | Verdict.Fail _ -> false

                    return verdict, commit
                })
