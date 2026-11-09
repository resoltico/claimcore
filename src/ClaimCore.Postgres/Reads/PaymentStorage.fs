namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Indexed bounded entry/context reads; Domain alone updates financial totals and counts.
module internal PaymentStorage =
    let private row value =
        let identity =
            DatabaseSession.namedValue<Guid> "payment_id" value
            |> DatabaseSession.admitted OperationId.create

        let date =
            DatabaseSession.namedValue<DateOnly> "payment_date" value |> CalendarDate.ofDate

        let amount =
            (DatabaseSession.namedValue<decimal> "paid_amount" value)
                .ToString("0.####", Globalization.CultureInfo.InvariantCulture)
            |> Amount.parse
            |> Result.defaultWith (fun _ ->
                raise (RecordCorruptException "Invalid projected payment amount."))

        PaymentEntry.create identity date amount
        |> Result.defaultWith (fun _ ->
            raise (RecordCorruptException "Invalid projected payment entry."))

    let active connection (parent, identity) token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT payment_id,payment_date,paid_amount FROM claimcore.case_payments WHERE case_id=$1 AND payment_id=$2 AND NOT removed AND payment_date IS NOT NULL"
                    [| box (CaseId.value parent); box identity |]
                    token

            return rows |> List.tryHead |> Option.map row
        }

    let latest connection (parent, limit) token =
        task {
            if limit < 1 || limit > PaymentPaging.MaximumLimit then
                invalidArg (nameof limit) "Payment read limit is out of range."

            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT payment_id,payment_date,paid_amount FROM claimcore.case_payments WHERE case_id=$1 AND NOT removed AND payment_date IS NOT NULL ORDER BY accepting_sequence DESC,effect_index DESC LIMIT $2"
                    [| box (CaseId.value parent); box limit |]
                    token

            return rows |> List.map row
        }

    let remainingDates connection (parent, selected) token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT (SELECT payment_date FROM claimcore.case_payments WHERE case_id=$1 AND payment_id<>$2 AND NOT removed AND payment_date IS NOT NULL ORDER BY payment_date ASC,payment_id ASC LIMIT 1) AS first_date,(SELECT payment_date FROM claimcore.case_payments WHERE case_id=$1 AND payment_id<>$2 AND NOT removed AND payment_date IS NOT NULL ORDER BY payment_date DESC,payment_id DESC LIMIT 1) AS last_date"
                    [| box (CaseId.value parent); box (OperationId.value selected) |]
                    token

            match rows with
            | [ row ] ->
                match
                    DatabaseSession.namedOptional<DateOnly> "first_date" row,
                    DatabaseSession.namedOptional<DateOnly> "last_date" row
                with
                | None, None -> return None
                | Some first, Some last ->
                    match
                        PaymentDateBounds.create
                            (CalendarDate.ofDate first)
                            (CalendarDate.ofDate last)
                    with
                    | Some bounds -> return Some bounds
                    | None ->
                        return
                            raise (RecordCorruptException "Invalid projected payment boundaries.")
                | _ ->
                    return raise (RecordCorruptException "Incomplete projected payment boundaries.")
            | _ -> return raise (RecordCorruptException "Missing payment boundary observation.")
        }

    let apply connection parent effect token =
        task {
            match effect with
            | None -> ()
            | Some(PaymentDifference.Recorded entry) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_payments(payment_id,case_id,accepting_sequence,effect_index,payment_date,paid_amount,removed) SELECT $1,$2,sequence,effect_index,$3::date,$4,false FROM claimcore.ledger WHERE operation_id=$1 AND subject_kind='CASE' AND subject_id=$2 AND act='RECORD_PAYMENT'"
                        [|
                            box (OperationId.value (PaymentEntry.id entry))
                            box (CaseId.value parent)
                            box (CalendarDate.value (PaymentEntry.date entry))
                            box (Amount.value (PaymentEntry.amount entry))
                        |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Payment accepting effect is absent.")
            | Some(PaymentDifference.Removed entry) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.case_payments SET removed=true WHERE case_id=$1 AND payment_id=$2 AND NOT removed AND payment_date IS NOT NULL"
                        [|
                            box (CaseId.value parent)
                            box (OperationId.value (PaymentEntry.id entry))
                        |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Selected active payment is absent.")
        }

    let erase connection parent token =
        task {
            let! _ =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.case_payments SET payment_date=NULL,paid_amount=NULL WHERE case_id=$1"
                    [| box (CaseId.value parent) |]
                    token

            return ()
        }
