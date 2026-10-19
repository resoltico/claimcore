namespace ClaimCore.Postgres

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

    let private amount index row =
        let text = required<string> index row

        let grammar =
            match FieldDefinitions.scalar (InputField.Business BusinessField.ClaimedAmount) with
            | ScalarRule.Amount rule -> rule.Grammar
            | _ -> invalidOp "Expected amount format."

        if
            not (
                Regex.IsMatch(
                    text,
                    "\\A" + grammar + "\\z",
                    RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
                )
            )
        then
            raise (RecordCorruptException "Invalid stored amount.")

        match
            Decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
        with
        | true, amount -> amount.ToString("0.####", CultureInfo.InvariantCulture)
        | _ -> raise (RecordCorruptException "Invalid stored amount.")

    let private date index row =
        (DatabaseSession.calendarDate index row)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let private optionalText formatter index (row: obj array) =
        if Convert.IsDBNull row[index] then
            None
        else
            Some(formatter index row)

    let caseColumns =
        [
            "case_id"
            "case_reference"
            "revision"
            "disposition"
            DatabaseSession.dateExpression "incident_date"
            DatabaseSession.dateExpression "incident_notification_date"
            "incident_country"
            "claimant_name"
            "insurer_name"
            "claimed_amount::text"
            "claimed_currency"
            DatabaseSession.dateExpression "payment_decision_date"
            "payable_amount::text"
            "payable_currency"
            DatabaseSession.dateExpression "payment_date"
            "status"
        ]
        |> String.concat ","

    let private fields reference row : CaseFields =
        {
            CaseReference = reference
            IncidentDate = date 4 row
            IncidentNotificationDate = date 5 row
            IncidentCountry = required 6 row
            ClaimantName = required 7 row
            InsurerName = required 8 row
            ClaimedAmount = amount 9 row
            ClaimedCurrency = required 10 row
            PaymentDecisionDate = optionalText date 11 row
            PayableAmount = optionalText amount 12 row
            PayableCurrency = optional<string> 13 row
            PaymentDate = optionalText date 14 row
            Status =
                CaseStatuses.tryParse (required<string> 15 row)
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid stored status."))
        }

    let caseRow (row: obj array) : StoredCase =
        let reference = required<string> 1 row

        let view =
            match required<string> 3 row with
            | "ERASED" ->
                if row[4..] |> Array.exists (Convert.IsDBNull >> not) then
                    raise (RecordCorruptException "Erased projection retains business fields.")

                CaseView.Erased
            | "ACTIVE" -> CaseView.Live(fields reference row, Disposition.Active)
            | "VOIDED" -> CaseView.Live(fields reference row, Disposition.Voided)
            | _ -> raise (RecordCorruptException "Invalid stored disposition.")

        let snapshot =
            {
                Reference = reference
                Revision = required<int64> 2 row
                View = view
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
        Roles.tryParse (required<string> 0 row)
        |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid stored role."))

    let actor connection principal token : System.Threading.Tasks.Task<ActorAuthority option> =
        task {
            let kind, issuer, subject = PrincipalKey.storageParts principal

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,enabled FROM claimcore.actors WHERE principal_kind=$1 AND issuer=$2 AND subject=$3"
                    [| box kind; box issuer; box subject |]
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                let id = required<Guid> 0 row

                let! grants =
                    DatabaseSession.rows
                        connection
                        "SELECT role,case_id FROM claimcore.grants WHERE actor_id=$1"
                        [| box id |]
                        token

                let grants =
                    grants
                    |> List.map (fun row ->
                        ({
                            Role = role row
                            Scope =
                                optional<Guid> 1 row
                                |> Option.map GrantScope.Case
                                |> Option.defaultValue GrantScope.Installation
                        }
                        : ActorGrant))

                return
                    Some
                        {
                            ActorId = id
                            Principal = principal
                            Enabled = required 1 row
                            Grants = grants
                        }
            | _ -> return raise (RecordCorruptException "Duplicate stored principal.")
        }

    let actorSummary row : ActorSummary =
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
