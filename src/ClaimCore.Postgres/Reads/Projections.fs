namespace ClaimCore.Postgres

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Application

module internal Projections =
    let private required<'T> index row = DatabaseSession.value<'T> index row
    let private optional<'T> index row = DatabaseSession.optional<'T> index row

    let private parsed =
        function
        | Ok value -> value
        | Error _ -> invalidOp "Invalid stored projection."

    let private amount index row =
        (required<decimal> index row).ToString("0.####", CultureInfo.InvariantCulture)

    let private date index row =
        (required<DateOnly> index row).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let private optionalText formatter index (row: obj array) =
        if Convert.IsDBNull row[index] then
            None
        else
            Some(formatter index row)

    let caseColumns =
        "case_id,case_reference,revision,disposition,incident_date,incident_notification_date,incident_country,claimant_name,insurer_name,claimed_amount,claimed_currency,payment_decision_date,payable_amount,payable_currency,payment_date,status"

    let caseRow row : StoredCase =
        let reference = required<string> 1 row
        let revision = required<int64> 2 row
        let disposition = required<string> 3 row

        let state =
            if disposition = "ERASED" then
                StoredCaseState.Erased
            else
                let status =
                    CaseStatuses.tryParse (required<string> 15 row)
                    |> Option.defaultWith (fun () -> invalidOp "Invalid stored status.")

                let view =
                    {
                        Version = revision
                        Fields =
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
                                Status = status
                            }
                    }

                Claim.restore view |> parsed |> ignore

                let disposition =
                    match disposition with
                    | "ACTIVE" -> CaseDisposition.Active
                    | "VOIDED" -> CaseDisposition.Voided
                    | _ -> invalidOp "Invalid stored disposition."

                StoredCaseState.Live(view, disposition)

        {
            CaseId = required 0 row
            Reference = reference
            Revision = revision
            State = state
        }

    let caseBy connection column value token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {caseColumns} FROM claimcore.cases WHERE {column}=$1")
                    [| value |]
                    token

            return rows |> List.tryHead |> Option.map caseRow
        }

    let actor connection principal token =
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
                    |> List.map (fun grant ->
                        {
                            Role =
                                Roles.tryParse (required<string> 0 grant)
                                |> Option.defaultWith (fun () -> invalidOp "Invalid stored role.")
                            Scope =
                                optional<Guid> 1 grant
                                |> Option.map GrantScope.Case
                                |> Option.defaultValue GrantScope.Installation
                        })

                return
                    Some
                        {
                            ActorId = id
                            Principal = principal
                            Enabled = required 1 row
                            Grants = grants
                        }
            | _ -> return invalidOp "Duplicate stored principal."
        }
