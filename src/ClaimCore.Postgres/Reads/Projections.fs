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

    let caseColumns =
        "case_id,case_reference,revision,state,snapshot,status,progress,search_text,"
        + DatabaseSession.instantExpression "changed_at"
        + " AS changed_at,"
        + DatabaseSession.dateExpression "notified_on"
        + " AS notified_on,responsible_actor,"
        + DatabaseSession.dateExpression "follow_up"
        + " AS follow_up,claimed_amount::text AS claimed_amount,claimed_currency,payable_amount::text AS payable_amount,payable_currency,(SELECT creation_reference FROM claimcore.ledger l WHERE l.subject_kind='CASE' AND l.subject_id=c.case_id AND l.case_revision=1) AS creation_reference"

    let caseStatus row =
        DatabaseSession.namedOptional<string> "status" row
        |> Option.map (fun value ->
            DurableNames.tryStatus value
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid indexed status.")))

    let caseProgress row =
        DatabaseSession.namedOptional<string> "progress" row
        |> Option.map (fun value ->
            DurableNames.tryProgress value
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
            NotifiedOn = caseNotifiedOn row
            Claimed = indexedMoney (caseClaimedAmount row) (caseClaimedCurrency row)
            Payable = indexedMoney (casePayableAmount row) (casePayableCurrency row)
        }


    let caseRowWithIndex _installationId index (row: Map<string, obj>) : StoredCase =
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
        DurableNames.tryRole (required<string> 0 row)
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
                            DurableNames.tryRole role
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
                        }
                    Grants = grants
                }

            AuthorityView.ofActor (Some stored)

    let actor connection principal (caseIds: CaseId list) token =
        task {
            let kind, issuer, subject = PrincipalKey.parts principal

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT a.actor_id,a.enabled,g.role,g.case_id FROM claimcore.actors a LEFT JOIN LATERAL (SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id IS NULL UNION ALL SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id=ANY($4::uuid[])) g ON true WHERE a.principal_kind=$1 AND a.issuer=$2 AND a.subject=$3"
                    [|
                        box (DurableNames.principalKind kind)
                        box issuer
                        box subject
                        box (caseIds |> List.distinct |> List.map CaseId.value |> List.toArray)
                    |]
                    token

            return authorityRows principal rows
        }

    let scopedCases connection principal readRoles (view: AuthorityView) token =
        task {
            match view.Actor with
            | None -> return view
            | Some actor ->
                let forbidden =
                    Authority.invalidRoles principal |> List.map DurableNames.role |> List.toArray

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=$1 AND g.case_id IS NOT NULL AND g.role=ANY($2::text[]) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=g.actor_id AND bad.case_id=g.case_id AND bad.role=ANY($3::text[])))"
                        [|
                            box (ActorId.value actor.ActorId)
                            box (readRoles |> List.map DurableNames.role |> List.toArray)
                            box forbidden
                        |]
                        token

                return
                    { view with
                        HasScopedCases = DatabaseSession.value<bool> 0 rows.Head
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
                (DurableNames.tryPrincipalKind (required 1 row)
                 |> Option.defaultWith (fun () ->
                     raise (RecordCorruptException "Invalid stored principal kind.")))
                |> fun kind -> PrincipalKey.ofKind kind (required 2 row) (required 3 row)
                |> parsed
            Enabled = required 4 row
        }

    let grantRow row : ScopedGrant * GrantPosition =
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

        { Role = role; Scope = scope }, { Role = role; CaseId = caseId }
