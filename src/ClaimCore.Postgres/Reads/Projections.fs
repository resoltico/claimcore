namespace ClaimCore.Postgres

open ClaimCore.RecordFormat

open System
open System.Globalization
open System.Text.RegularExpressions
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application

module internal Projections =
    let private required<'T> index row = DatabaseSession.value<'T> index row
    let private optional<'T> index row = DatabaseSession.optional<'T> index row

    let private parsed =
        function
        | Ok value -> value
        | Error _ -> raise (RecordCorruptException "Invalid stored projection.")

    let profile (name: string option) (contact: string option) =
        match name, contact with
        | None, None -> None
        | Some name, contact ->
            ActorProfile.restore
                {
                    DisplayName = name
                    Contact = contact
                }
            |> parsed
            |> Some
        | None, Some _ -> raise (RecordCorruptException "Contact without an actor display name.")

    let caseColumns =
        "case_id,case_reference,revision,state,snapshot,status,progress,search_text,work_search_text,"
        + DatabaseSession.instantExpression "changed_at"
        + " AS changed_at,"
        + DatabaseSession.dateExpression "notified_on"
        + " AS notified_on,responsible_actor,"
        + DatabaseSession.dateExpression "follow_up"
        + " AS follow_up,claimed_amount::text AS claimed_amount,claimed_currency,payable_amount::text AS payable_amount,payable_currency,paid_amount::text AS paid_amount,(SELECT creation_reference FROM claimcore.ledger l WHERE l.subject_kind='CASE' AND l.subject_id=c.case_id AND l.case_revision=1) AS creation_reference"

    let caseStatus row =
        DatabaseSession.namedOptional<string> "status" row
        |> Option.map (fun value ->
            (Vocabulary.tryParse CaseStatus.vocabulary) value
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid indexed status.")))

    let caseProgress row =
        DatabaseSession.namedOptional<string> "progress" row
        |> Option.map (fun value ->
            (Vocabulary.tryParse CaseProgress.vocabulary) value
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid indexed progress.")))

    let caseChangedAt row =
        DatabaseSession.namedRecordedInstant "changed_at" row

    let caseSearchText row =
        DatabaseSession.namedOptional<string> "search_text" row

    let caseNotifiedOn row =
        DatabaseSession.namedOptionalCalendarDate "notified_on" row
        |> Option.map CalendarDate.ofDate

    let caseResponsibleActor row =
        DatabaseSession.namedOptional<Guid> "responsible_actor" row
        |> Option.map (DatabaseSession.admitted ActorId.create)

    let caseFollowUp row =
        DatabaseSession.namedOptionalCalendarDate "follow_up" row
        |> Option.map CalendarDate.ofDate

    let private amount column row =
        DatabaseSession.namedOptional<string> column row
        |> Option.map (Amount.parse >> parsed)

    let private currency column row =
        DatabaseSession.namedOptional<string> column row
        |> Option.map (Currency.parse >> parsed)

    let caseClaimedAmount row = amount "claimed_amount" row
    let caseClaimedCurrency row = currency "claimed_currency" row
    let casePayableAmount row = amount "payable_amount" row
    let casePaidAmount row = amount "paid_amount" row
    let casePayableCurrency row = currency "payable_currency" row

    let indexedMoney amount currency =
        match amount, currency with
        | None, None -> None
        | Some amount, Some currency -> Some(Money.create amount currency)
        | _ -> raise (RecordCorruptException "Incomplete indexed amount and currency.")

    let caseIndex row : CaseIndex =
        {
            Responsible = caseResponsibleActor row
            FollowUp = caseFollowUp row
            Status = caseStatus row
            Progress = caseProgress row
            ChangedAt = caseChangedAt row
            SearchText = caseSearchText row
            WorkSearchText = DatabaseSession.namedOptional<string> "work_search_text" row
            NotifiedOn = caseNotifiedOn row
            Claimed = indexedMoney (caseClaimedAmount row) (caseClaimedCurrency row)
            Payable = indexedMoney (casePayableAmount row) (casePayableCurrency row)
            Paid = indexedMoney (casePaidAmount row) (casePayableCurrency row)
        }


    let caseRowWithIndex _installationId index (row: NamedRow) : StoredCase =
        let reference =
            DatabaseSession.namedValue<string> "case_reference" row
            |> CaseReference.parse
            |> parsed

        let revision =
            DatabaseSession.namedValue<int64> "revision" row
            |> DatabaseSession.admitted Revision.create

        let state = DatabaseSession.namedValue<string> "state" row

        let id =
            DatabaseSession.namedValue<Guid> "case_id" row
            |> DatabaseSession.admitted CaseId.create

        let bytes = DatabaseSession.namedOptional<byte array> "snapshot" row

        let snapshot =
            match state, bytes with
            | "ERASED", None ->
                {
                    CaseId = id
                    Reference = reference
                    Revision = revision
                    State = CaseState.Erased
                }
            | ("ACTIVE" | "VOIDED"), Some bytes when bytes.Length > 0 && bytes.Length <= 32768 ->
                ClaimCore.RecordFormat.CaseRecord.decodeSnapshot bytes |> parsed
            | _ -> raise (RecordCorruptException "Invalid stored snapshot shape.")

        if
            snapshot.Reference <> reference
            || snapshot.Revision <> revision
            || CaseState.token snapshot.State <> state
            || snapshot.CaseId <> id
            || DatabaseSession.namedOptional<string> "creation_reference" row
               <> Some(CaseReference.format reference)
        then
            raise (RecordCorruptException "Stored snapshot disagrees with projection identity.")

        Case.ofAccepted snapshot |> parsed |> ignore

        {
            CaseId = id
            Snapshot = snapshot
            SnapshotBytes = bytes |> Option.defaultValue Array.empty
            Index = index
        }

    let caseRow installationId row =
        caseRowWithIndex installationId (caseIndex row) row

    let caseById connection installationId id token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {caseColumns} FROM claimcore.cases c WHERE case_id=$1")
                    [| box (CaseId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some(caseRow installationId row)
            | _ -> return raise (RecordCorruptException "Duplicate case projection.")
        }

    let caseByReference connection installationId reference token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {caseColumns} FROM claimcore.cases c WHERE upper(case_reference COLLATE \"C\")=$1")
                    [| box (CaseReference.key reference) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some(caseRow installationId row)
            | _ -> return raise (RecordCorruptException "Duplicate reference projection.")
        }

    let private role row =
        (Vocabulary.tryParse Role.vocabulary) (required<string> 0 row)
        |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid stored role."))

    let private authorityRows principal (rows: obj array list) =
        match rows with
        | [] -> AuthorityView.empty
        | first :: _ ->
            let grants: ActorGrant list =
                rows
                |> List.choose (fun row ->
                    optional<string> 2 row
                    |> Option.map (fun role ->
                        let role =
                            (Vocabulary.tryParse Role.vocabulary) role
                            |> Option.defaultWith (fun () ->
                                raise (RecordCorruptException "Invalid stored role."))

                        {
                            Role = role
                            Scope =
                                optional<Guid> 3 row
                                |> Option.map (
                                    DatabaseSession.admitted CaseId.create >> Scope.Case
                                )
                                |> Option.defaultValue Scope.Installation
                        }))

            let stored: ActorAuthority =
                {
                    Actor =
                        {
                            ActorId = required 0 first |> DatabaseSession.admitted ActorId.create
                            Principal = principal
                            Enabled = required 1 first
                            Profile = profile (optional<string> 4 first) (optional<string> 5 first)
                        }
                    Grants = grants
                }

            AuthorityView.ofActor (Some stored)

    let private actorQuery columns =
        $"SELECT {columns} FROM claimcore.actors a LEFT JOIN LATERAL (SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id IS NULL UNION ALL SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id=ANY($4::uuid[])) g ON true WHERE a.principal_kind=$1 AND a.issuer=$2 AND a.subject=$3"

    let private actorParameters principal (caseIds: CaseId list) =
        let kind, issuer, subject = PrincipalKey.parts principal

        [|
            box ((Vocabulary.token PrincipalKind.vocabulary) kind)
            box issuer
            box subject
            box (caseIds |> List.distinct |> List.map CaseId.value |> List.toArray)
        |]

    let private actorColumns =
        "a.actor_id,a.enabled,g.role,g.case_id,a.display_name,a.contact"

    let actor connection principal (caseIds: CaseId list) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    (actorQuery actorColumns)
                    (actorParameters principal caseIds)
                    token

            return authorityRows principal rows
        }

    let private globalActorQuery principal =
        let kind, issuer, subject = PrincipalKey.parts principal

        let scoped =
            "EXISTS(SELECT 1 FROM claimcore.grants scoped WHERE scoped.actor_id=a.actor_id AND scoped.case_id IS NOT NULL AND scoped.role=ANY($4::text[]) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=scoped.actor_id AND bad.case_id=scoped.case_id AND bad.role=ANY($5::text[])))"

        let sql =
            $"SELECT {actorColumns},{scoped},EXISTS(SELECT 1 FROM claimcore.grants assigned WHERE assigned.actor_id=a.actor_id) FROM claimcore.actors a LEFT JOIN claimcore.grants g ON g.actor_id=a.actor_id AND g.case_id IS NULL WHERE a.principal_kind=$1 AND a.issuer=$2 AND a.subject=$3"

        let parameters =
            [|
                box ((Vocabulary.token PrincipalKind.vocabulary) kind)
                box issuer
                box subject
                box (Authority.readRoles principal |> List.map Role.token |> List.toArray)
                box (Authority.invalidRoles principal |> List.map Role.token |> List.toArray)
            |]

        sql, parameters

    let private globalAuthority principal rows =
        let actorRows =
            rows
            |> List.filter (fun row -> DatabaseSession.optional<Guid> 0 row |> Option.isSome)

        let view = authorityRows principal actorRows

        match actorRows with
        | [] -> view
        | first :: _ ->
            { view with
                HasScopedCases = DatabaseSession.value<bool> 6 first
                HasAssignedRoles = DatabaseSession.value<bool> 7 first
            }

    /// Global visibility and actor/grant facts belong to the same read snapshot, without a second round trip.
    let actorWithVisibility connection principal token =
        task {
            let sql, parameters = globalActorQuery principal
            let! rows = DatabaseSession.rows connection sql parameters token
            return globalAuthority principal rows
        }

    // Both directory pages and own context summarize raw assignments, independently of eligibility.
    let private roleSummaryQuery predicate =
        $"SELECT g.role,bool_or(g.case_id IS NULL) AS installation,count(g.case_id) AS case_count FROM claimcore.grants g WHERE {predicate} GROUP BY g.role"

    let private roleSummary role installation count =
        {
            ActorRoleSummary.Role =
                (Vocabulary.tryParse Role.vocabulary) role
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Unknown assigned role."))
            Installation = installation
            CaseCount = count
        }

    let private roleSummaryJson (raw: string) =
        use document = System.Text.Json.JsonDocument.Parse raw

        document.RootElement.EnumerateArray()
        |> Seq.map (fun value ->
            roleSummary
                (value.GetProperty("role").GetString()
                 |> Option.ofObj
                 |> Option.defaultWith (fun () ->
                     raise (RecordCorruptException "Missing assigned role.")))
                (value.GetProperty("installation").GetBoolean())
                (value.GetProperty("case_count").GetInt64()))
        |> Seq.toList
        |> ActorRoleSummary.ordered

    let globalReadFacts connection principal token =
        task {
            let actor, parameters = globalActorQuery principal

            let roles = roleSummaryQuery "g.actor_id=actor.actor_id"

            let sql =
                $"WITH current_mode AS MATERIALIZED(SELECT active,generation,count(*) OVER() AS mode_count FROM claimcore.register_maintenance) SELECT actor.*,m.active,m.generation,m.mode_count,(SELECT COALESCE(jsonb_agg(to_jsonb(r)),'[]'::jsonb)::text FROM ({roles}) r) FROM current_mode m LEFT JOIN LATERAL({actor}) actor ON true"

            let! rows = DatabaseSession.preparedRows connection sql parameters token

            let mode =
                match rows with
                | first :: _ when DatabaseSession.value<int64> 10 first = 1L ->
                    RegisterMaintenance.create
                        (DatabaseSession.value<bool> 8 first)
                        (DatabaseSession.value<int64> 9 first)
                    |> Result.defaultWith (fun _ ->
                        raise (RecordCorruptException "Invalid register maintenance state."))
                | _ ->
                    raise (
                        RecordCorruptException "Register maintenance state is absent or duplicated."
                    )

            return
                {
                    GlobalReadFacts.Authority = globalAuthority principal rows
                    AssignedRoles = roleSummaryJson (DatabaseSession.value<string> 11 rows.Head)
                    Maintenance = mode
                }
        }

    let operationCases entry =
        match entry |> Option.map (fun (entry: StoredEntry) -> entry.Subject) with
        | Some(EntryTarget.Case(id, _, _, _)) -> [ id ]
        | _ -> []

    let actorRow row : Actor =
        {
            ActorId = required 0 row |> DatabaseSession.admitted ActorId.create
            Principal =
                ((Vocabulary.tryParse PrincipalKind.vocabulary) (required 1 row)
                 |> Option.defaultWith (fun () ->
                     raise (RecordCorruptException "Invalid stored principal kind.")))
                |> fun kind -> PrincipalKey.ofKind kind (required 2 row) (required 3 row)
                |> parsed
            Enabled = required 4 row
            Profile = profile (optional<string> 5 row) (optional<string> 6 row)
        }

    let grantRow row : GrantRow * GrantPosition =
        let role = role row

        let caseId =
            optional<Guid> 2 row |> Option.map (DatabaseSession.admitted CaseId.create)

        let scope =
            match caseId, optional<string> 1 row, optional<string> 3 row with
            | None, None, None -> GrantScope.Installation
            | Some id, Some reference, Some retained when reference = retained ->
                match CaseReference.parse reference with
                | Ok reference -> GrantScope.Case(id, reference)
                | Error _ -> raise (RecordCorruptException "Invalid grant reference.")
            | _ ->
                raise (
                    RecordCorruptException
                        "Grant projection disagrees with retained creation identity."
                )

        {
            GrantRow.Grant = { Role = role; Scope = scope }
            GrantedAt = DatabaseSession.recordedInstant 4 row
            GrantedBy = DatabaseSession.value<Guid> 5 row |> DatabaseSession.admitted ActorId.create
        },
        { Role = role; CaseId = caseId }

    let actorRoleSummaries connection ids token =
        task {
            if List.isEmpty ids then
                return Map.empty
            else
                let roles = roleSummaryQuery "g.actor_id=a.actor_id"

                let! rows =
                    DatabaseSession.rows
                        connection
                        ($"SELECT a.actor_id,r.* FROM claimcore.actors a LEFT JOIN LATERAL({roles}) r ON true WHERE a.actor_id=ANY($1::uuid[]) ORDER BY a.actor_id,r.role")
                        [| box (ids |> List.map ActorId.value |> List.distinct |> List.toArray) |]
                        token

                return
                    rows
                    |> List.groupBy (fun row ->
                        DatabaseSession.value<Guid> 0 row
                        |> DatabaseSession.admitted ActorId.create)
                    |> List.map (fun (id, rows) ->
                        id,
                        rows
                        |> List.choose (fun row ->
                            DatabaseSession.optional<string> 1 row
                            |> Option.map (fun role ->
                                roleSummary
                                    role
                                    (DatabaseSession.value<bool> 2 row)
                                    (DatabaseSession.value<int64> 3 row))))
                    |> Map.ofList
        }
