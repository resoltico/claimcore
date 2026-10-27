namespace ClaimCore.Postgres

open System
open System.Globalization
open ClaimCore.Domain

/// Column names derive from record keys; reads identify cells by name and bindings share the column sequence.
module internal CaseProjectionLayout =
    let name field =
        BusinessField.key field
        |> Seq.collect (fun character ->
            if Char.IsAsciiLetterUpper character then
                [ '_'; Char.ToLowerInvariant character ]
            else
                [ character ])
        |> Array.ofSeq
        |> String

    let private column (field: FieldDeclaration) = name field.Field
    let private retained, payload = Record.fields |> List.partition _.RetainedOnErasure
    let payloadColumns = payload |> List.map column

    let private columns =
        [ "case_id" ]
        @ (retained |> List.map column)
        @ [ "revision"; "state" ]
        @ (payload |> List.map column)

    let private selected (row: FieldDeclaration) =
        match row.Kind with
        | FieldValueKind.CalendarDate ->
            DatabaseSession.dateExpression (column row) + " AS " + column row
        | FieldValueKind.Amount -> column row + "::text AS " + column row
        | _ -> column row

    let select =
        [ "case_id" ]
        @ (retained |> List.map selected)
        @ [ "revision"; "state" ]
        @ (payload |> List.map selected)
        |> String.concat ","

    let private placeholder index = "$" + string (index + 1)

    let insert =
        let names = String.concat "," columns

        let parameters =
            columns |> List.mapi (fun index _ -> placeholder index) |> String.concat ","

        $"INSERT INTO claimcore.cases ({names}) VALUES ({parameters})"

    let update =
        let assignments =
            columns
            |> List.mapi (fun index name -> name + "=" + placeholder index)
            |> List.tail
            |> String.concat ","

        $"UPDATE claimcore.cases SET {assignments} WHERE case_id=$1"

    let erase =
        let assignments =
            payload |> List.map (fun row -> column row + "=NULL") |> String.concat ","

        $"UPDATE claimcore.cases SET revision=$2,state='ERASED',{assignments} WHERE case_id=$1"

    // This is an exhaustive conversion of named fields, not conditional business policy.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private parameter (fields: CaseFields) =
        function
        | BusinessField.CaseReference -> box (CaseReference.format fields.CaseReference)
        | BusinessField.Status -> box (CaseStatus.token fields.Status)
        | BusinessField.IncidentDate -> box (CalendarDate.value fields.Registration.IncidentDate)
        | BusinessField.IncidentNotificationDate ->
            box (CalendarDate.value fields.Registration.IncidentNotificationDate)
        | BusinessField.IncidentCountry ->
            box (CountryCode.format fields.Registration.IncidentCountry)
        | BusinessField.ClaimantName -> box (PartyName.format fields.Registration.ClaimantName)
        | BusinessField.InsurerName -> box (PartyName.format fields.Registration.InsurerName)
        | BusinessField.ClaimedAmount -> box (Money.value fields.Registration.ClaimedAmount)
        | BusinessField.ClaimedCurrency -> box (Money.currency fields.Registration.ClaimedAmount)
        | BusinessField.PaymentDecisionDate ->
            DatabaseSession.parameter (
                fields.Decision
                |> Option.map (fun value -> CalendarDate.value value.PaymentDecisionDate)
            )
        | BusinessField.PayableAmount ->
            DatabaseSession.parameter (
                fields.Decision |> Option.map (fun value -> Money.value value.PayableAmount)
            )
        | BusinessField.PayableCurrency ->
            DatabaseSession.parameter (
                fields.Decision |> Option.map (fun value -> Money.currency value.PayableAmount)
            )
        | BusinessField.PaymentDate ->
            DatabaseSession.parameter (
                fields.Payment |> Option.map (fun value -> CalendarDate.value value.PaymentDate)
            )

    let parameters id (snapshot: CaseSnapshot) =
        let fields, state =
            match snapshot.State with
            | CaseState.Active(fields) -> fields, "ACTIVE"
            | CaseState.Voided(fields) -> fields, "VOIDED"
            | CaseState.Erased -> invalidArg (nameof snapshot) "Use the erasure projection."

        let bind (row: FieldDeclaration) = parameter fields row.Field

        [ box id ]
        @ (retained |> List.map bind)
        @ [ box snapshot.Revision; box state ]
        @ (payload |> List.map bind)
        |> List.toArray
