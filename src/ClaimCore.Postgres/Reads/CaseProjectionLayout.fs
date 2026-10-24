namespace ClaimCore.Postgres

open System
open System.Globalization
open ClaimCore.Contract
open ClaimCore.Domain

/// One positional layout for case projection reads, bindings and payload erasure.
module internal CaseProjectionLayout =
    let private column (row: FieldRow) =
        row.Key
        |> Seq.collect (fun character ->
            if Char.IsAsciiLetterUpper character then
                [ '_'; Char.ToLowerInvariant character ]
            else
                [ character ])
        |> Array.ofSeq
        |> String

    let private retained, payload =
        FieldTable.rows |> List.partition (fun row -> row.Group = FieldGroup.Identity)

    let revisionOrdinal = 1 + retained.Length
    let dispositionOrdinal = revisionOrdinal + 1
    let payloadOrdinal = dispositionOrdinal + 1

    let private positioned =
        (retained |> List.mapi (fun index row -> row, index + 1))
        @ (payload |> List.mapi (fun index row -> row, index + payloadOrdinal))

    let ordinal field =
        positioned |> List.find (fun (row, _) -> row.Field = field) |> snd

    let private columns =
        [ "case_id" ]
        @ (retained |> List.map column)
        @ [ "revision"; "disposition" ]
        @ (payload |> List.map column)

    let private selected (row: FieldRow) =
        match row.Kind with
        | ScalarKind.Date -> DatabaseSession.dateExpression (column row)
        | ScalarKind.Amount -> column row + "::text"
        | _ -> column row

    let select =
        [ "case_id" ]
        @ (retained |> List.map selected)
        @ [ "revision"; "disposition" ]
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

        $"UPDATE claimcore.cases SET revision=$2,disposition='ERASED',{assignments} WHERE case_id=$1"

    let private parameter (row: FieldRow) value =
        match value with
        | None -> box DBNull.Value
        | Some text ->
            match row.Kind with
            | ScalarKind.Date ->
                box (DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            | ScalarKind.Amount -> box (Decimal.Parse(text, CultureInfo.InvariantCulture))
            | _ -> box text

    let parameters id (snapshot: CaseSnapshot) =
        let fields, disposition =
            match snapshot.View with
            | CaseView.Live(fields, Disposition.Active) -> fields, "ACTIVE"
            | CaseView.Live(fields, Disposition.Voided) -> fields, "VOIDED"
            | CaseView.Erased -> invalidArg (nameof snapshot) "Use the erasure projection."

        let values = FieldDefinitions.values fields |> Map.ofList

        let bind row =
            parameter row (Map.find row.Field values)

        [ box id ]
        @ (retained |> List.map bind)
        @ [ box snapshot.Revision; box disposition ]
        @ (payload |> List.map bind)
        |> List.toArray
