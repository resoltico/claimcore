namespace ClaimCore.Postgres

open ClaimCore.Contract

open System
open System.Globalization
open System.Text.RegularExpressions
open ClaimCore.Domain
open ClaimCore.Application

module internal Projections =
    let private required<'T> index row = DatabaseSession.value<'T> index row
    let private optional<'T> index row = DatabaseSession.optional<'T> index row

    let private parsed =
        function
        | Ok value -> value
        | Error _ -> raise (RecordCorruptException "Invalid stored projection.")

    let private date index row =
        DatabaseSession.calendarDate index row |> CalendarDate.ofDate

    let private optionalDate index (row: obj array) =
        if Convert.IsDBNull row[index] then
            None
        else
            Some(date index row)

    let caseColumns = CaseProjectionLayout.select

    let private fields reference row : CaseFields =
        let decision =
            match
                optionalDate (CaseProjectionLayout.ordinal BusinessField.PaymentDecisionDate) row,
                optional<string> (CaseProjectionLayout.ordinal BusinessField.PayableAmount) row,
                optional<string> (CaseProjectionLayout.ordinal BusinessField.PayableCurrency) row
            with
            | None, None, None -> None
            | Some date, Some amount, Some currency ->
                Some
                    {
                        PaymentDecisionDate = date
                        PayableAmount = Money.parse amount currency |> parsed
                    }
            | _ -> raise (RecordCorruptException "Incomplete stored decision.")

        {
            CaseReference = reference
            Status =
                CaseStatuses.parse (
                    required<string> (CaseProjectionLayout.ordinal BusinessField.Status) row
                )
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid stored status."))
            Registration =
                {
                    IncidentDate =
                        date (CaseProjectionLayout.ordinal BusinessField.IncidentDate) row
                    IncidentNotificationDate =
                        date
                            (CaseProjectionLayout.ordinal BusinessField.IncidentNotificationDate)
                            row
                    IncidentCountry =
                        required<string>
                            (CaseProjectionLayout.ordinal BusinessField.IncidentCountry)
                            row
                        |> CountryCode.parse
                        |> parsed
                    ClaimantName =
                        required<string>
                            (CaseProjectionLayout.ordinal BusinessField.ClaimantName)
                            row
                        |> PartyName.parse
                        |> parsed
                    InsurerName =
                        required<string>
                            (CaseProjectionLayout.ordinal BusinessField.InsurerName)
                            row
                        |> PartyName.parse
                        |> parsed
                    ClaimedAmount =
                        Money.parse
                            (required<string>
                                (CaseProjectionLayout.ordinal BusinessField.ClaimedAmount)
                                row)
                            (required<string>
                                (CaseProjectionLayout.ordinal BusinessField.ClaimedCurrency)
                                row)
                        |> parsed
                }
            Decision = decision
            Payment =
                optionalDate (CaseProjectionLayout.ordinal BusinessField.PaymentDate) row
                |> Option.map (fun date -> { PaymentDate = date })
        }

    let caseRow (row: obj array) : StoredCase =
        let reference =
            required<string> (CaseProjectionLayout.ordinal BusinessField.CaseReference) row
            |> CaseReference.parse
            |> parsed

        let view =
            match required<string> CaseProjectionLayout.stateOrdinal row with
            | "ERASED" ->
                if
                    row[CaseProjectionLayout.payloadOrdinal ..]
                    |> Array.exists (Convert.IsDBNull >> not)
                then
                    raise (RecordCorruptException "Erased projection retains business fields.")

                CaseState.Erased
            | "ACTIVE" -> CaseState.Active(fields reference row)
            | "VOIDED" -> CaseState.Voided(fields reference row)
            | _ -> raise (RecordCorruptException "Invalid stored state.")

        let snapshot =
            {
                Reference = reference
                Revision = required<int64> CaseProjectionLayout.revisionOrdinal row
                State = view
            }

        Case.ofAccepted snapshot |> parsed |> ignore

        {
            CaseId = required 0 row
            Snapshot = snapshot
        }

    let caseById connection id token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {caseColumns} FROM claimcore.cases WHERE case_id=$1")
                    [| box id |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some(caseRow row)
            | _ -> return raise (RecordCorruptException "Duplicate case projection.")
        }

    let private role row =
        RoleTokens.parse (required<string> 0 row)
        |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid stored role."))

    let private authorityRows principal (rows: obj array list) =
        match rows with
        | [] -> AuthorityView.empty
        | first :: _ ->
            let grants =
                rows
                |> List.choose (fun row ->
                    optional<string> 2 row
                    |> Option.map (fun role ->
                        let role =
                            RoleTokens.parse role
                            |> Option.defaultWith (fun () ->
                                raise (RecordCorruptException "Invalid stored role."))

                        {
                            Role = role
                            Scope =
                                optional<Guid> 3 row
                                |> Option.map GrantScope.Case
                                |> Option.defaultValue GrantScope.Installation
                        }))

            let stored: ActorAuthority =
                {
                    ActorId = required 0 first
                    Principal = principal
                    Enabled = required 1 first
                    Grants = grants
                }

            AuthorityView.ofActor (Some stored)

    let actor connection principal (caseIds: Guid list) token =
        task {
            let kind, issuer, subject = PrincipalKey.storageParts principal

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT a.actor_id,a.enabled,g.role,g.case_id FROM claimcore.actors a LEFT JOIN LATERAL (SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id IS NULL UNION ALL SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id=ANY($4::uuid[])) g ON true WHERE a.principal_kind=$1 AND a.issuer=$2 AND a.subject=$3"
                    [|
                        box kind
                        box issuer
                        box subject
                        box (caseIds |> List.distinct |> List.toArray)
                    |]
                    token

            return authorityRows principal rows
        }

    let assigned connection principal readRoles (view: AuthorityView) token =
        task {
            match view.Actor with
            | None -> return view
            | Some actor ->
                let forbidden =
                    ActorAuthorization.invalidRoles principal
                    |> List.map RoleTokens.token
                    |> List.toArray

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=$1 AND g.case_id IS NOT NULL AND g.role=ANY($2::text[]) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=g.actor_id AND bad.case_id=g.case_id AND bad.role=ANY($3::text[])))"
                        [|
                            box actor.ActorId
                            box (readRoles |> List.map RoleTokens.token |> List.toArray)
                            box forbidden
                        |]
                        token

                return
                    { view with
                        HasAssignedCases = DatabaseSession.value<bool> 0 rows.Head
                    }
        }

    let operationCases entry =
        match entry |> Option.map (fun (entry: StoredEntry) -> entry.Subject) with
        | Some(StoredSubject.Case(id, _, _, _)) -> [ id ]
        | _ -> []

    let actorRow row : Actor =
        {
            ActorId = required 0 row
            Principal =
                PrincipalKey.fromStorage (required 1 row) (required 2 row) (required 3 row)
                |> parsed
            Enabled = required 4 row
        }

    let grantRow row : AuthorityGrant * GrantPosition =
        let role = role row
        let caseId = optional<Guid> 2 row

        let scope =
            match caseId, optional<string> 1 row with
            | None, None -> GrantTarget.Installation
            | Some _, Some reference when CaseReference.validate reference |> Result.isOk ->
                GrantTarget.CaseReference reference
            | _ -> raise (RecordCorruptException "Invalid grant projection.")

        { Role = role; Scope = scope }, { Role = role; CaseId = caseId }
