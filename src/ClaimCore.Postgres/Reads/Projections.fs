namespace ClaimCore.Postgres

open ClaimCore.RecordFormat

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

    let private date field row =
        DatabaseSession.namedCalendarDate (CaseProjectionLayout.name field) row
        |> CalendarDate.ofDate

    let private field<'T> field row =
        DatabaseSession.namedValue<'T> (CaseProjectionLayout.name field) row

    let private optionalField<'T> field row =
        DatabaseSession.namedOptional<'T> (CaseProjectionLayout.name field) row

    let private optionalDate field row =
        DatabaseSession.namedOptional<string> (CaseProjectionLayout.name field) row
        |> Option.map (fun _ -> date field row)

    let caseColumns = CaseProjectionLayout.select

    let private fields reference row : CaseFields =
        let decision =
            match
                optionalDate BusinessField.PaymentDecisionDate row,
                optionalField<string> BusinessField.PayableAmount row,
                optionalField<string> BusinessField.PayableCurrency row
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
                CaseStatus.parse (field<string> BusinessField.Status row)
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid stored status."))
            Registration =
                {
                    IncidentDate = date BusinessField.IncidentDate row
                    IncidentNotificationDate = date BusinessField.IncidentNotificationDate row
                    IncidentCountry =
                        field<string> BusinessField.IncidentCountry row
                        |> CountryCode.parse
                        |> parsed
                    ClaimantName =
                        field<string> BusinessField.ClaimantName row |> PartyName.parse |> parsed
                    InsurerName =
                        field<string> BusinessField.InsurerName row |> PartyName.parse |> parsed
                    ClaimedAmount =
                        Money.parse
                            (field<string> BusinessField.ClaimedAmount row)
                            (field<string> BusinessField.ClaimedCurrency row)
                        |> parsed
                }
            Decision = decision
            Payment =
                optionalDate BusinessField.PaymentDate row
                |> Option.map (fun date -> { PaymentDate = date })
        }

    let caseRow (row: Map<string, obj>) : StoredCase =
        let reference =
            field<string> BusinessField.CaseReference row |> CaseReference.parse |> parsed

        let view =
            match DatabaseSession.namedValue<string> "state" row with
            | "ERASED" ->
                if
                    CaseProjectionLayout.payloadColumns
                    |> List.exists (fun name ->
                        not (Convert.IsDBNull(DatabaseSession.namedValue<obj> name row)))
                then
                    raise (RecordCorruptException "Erased projection retains business fields.")

                CaseState.Erased
            | "ACTIVE" -> CaseState.Active(fields reference row)
            | "VOIDED" -> CaseState.Voided(fields reference row)
            | _ -> raise (RecordCorruptException "Invalid stored state.")

        let snapshot =
            {
                Reference = reference
                Revision = DatabaseSession.namedValue<int64> "revision" row
                State = view
            }

        Case.ofAccepted snapshot |> parsed |> ignore

        {
            CaseId = DatabaseSession.namedValue "case_id" row
            Snapshot = snapshot
        }

    let caseById connection id token =
        task {
            let! rows =
                DatabaseSession.namedRows
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
            let kind, issuer, subject = PrincipalKey.parts principal

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT a.actor_id,a.enabled,g.role,g.case_id FROM claimcore.actors a LEFT JOIN LATERAL (SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id IS NULL UNION ALL SELECT role,case_id FROM claimcore.grants WHERE actor_id=a.actor_id AND case_id=ANY($4::uuid[])) g ON true WHERE a.principal_kind=$1 AND a.issuer=$2 AND a.subject=$3"
                    [|
                        box (DurableNames.principalKind kind)
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
                    Authority.invalidRoles principal |> List.map Role.token |> List.toArray

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=$1 AND g.case_id IS NOT NULL AND g.role=ANY($2::text[]) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=g.actor_id AND bad.case_id=g.case_id AND bad.role=ANY($3::text[])))"
                        [|
                            box actor.ActorId
                            box (readRoles |> List.map Role.token |> List.toArray)
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
        | Some(EntryTarget.Case(id, _, _, _)) -> [ id ]
        | _ -> []

    let actorRow row : Actor =
        {
            ActorId = required 0 row
            Principal =
                (DurableNames.tryPrincipalKind (required 1 row)
                 |> Option.defaultWith (fun () ->
                     raise (RecordCorruptException "Invalid stored principal kind.")))
                |> fun kind -> PrincipalKey.ofKind kind (required 2 row) (required 3 row)
                |> parsed
            Enabled = required 4 row
        }

    let grantRow row : AuthorityGrant * GrantPosition =
        let role = role row
        let caseId = optional<Guid> 2 row

        let scope =
            match caseId, optional<string> 1 row with
            | None, None -> GrantTarget.Installation
            | Some _, Some reference when
                (CaseReference.parse
                 >> Result.map ignore
                 >> Result.mapError (fun violation ->
                     Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation)))
                    reference
                |> Result.isOk
                ->
                GrantTarget.CaseReference reference
            | _ -> raise (RecordCorruptException "Invalid grant projection.")

        { Role = role; Scope = scope }, { Role = role; CaseId = caseId }
