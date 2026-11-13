namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts

/// Owned payment rows and scoped disclosure reads; Domain alone updates financial facts.
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

    let apply connection parent effect token =
        task {
            match effect with
            | None -> ()
            | Some(PaymentDifference.Recorded entry) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_payments(payment_id,case_id,accepting_sequence,effect_index,payment_date,paid_amount,removed) SELECT $1,$2,l.sequence,e.effect_index,$3::date,$4,false FROM claimcore.ledger l JOIN claimcore.ledger_effects e ON e.operation_sequence=l.sequence WHERE l.operation_id=$1 AND e.subject_kind='CASE' AND e.subject_id=$2 AND e.act='case.recordPayment'"
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

    let page
        connection
        (query: ClaimCore.Application.PaymentPageQuery)
        token
        : System.Threading.Tasks.Task<
            ClaimCore.Application.Page<PaymentEntry * EffectPosition, EffectPosition>
           >
        =
        task {
            let beforeSequence =
                query.Before |> Option.map (EffectPosition.sequence >> Sequence.value)

            let beforeIndex = query.Before |> Option.map EffectPosition.index

            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT payment_id,payment_date,paid_amount,accepting_sequence,effect_index FROM claimcore.case_payments WHERE case_id=$1 AND NOT removed AND payment_date IS NOT NULL AND ($2::bigint IS NULL OR (accepting_sequence,effect_index)<($2::bigint,$3::integer)) ORDER BY accepting_sequence DESC,effect_index DESC LIMIT $4"
                    [|
                        box (CaseId.value query.CaseId)
                        DatabaseSession.parameter beforeSequence
                        DatabaseSession.parameter beforeIndex
                        box (query.Limit + 1)
                    |]
                    token

            let visible = rows |> List.truncate query.Limit

            let items =
                visible
                |> List.map (fun value ->
                    let sequence =
                        DatabaseSession.namedValue<int64> "accepting_sequence" value
                        |> DatabaseSession.admitted Sequence.create

                    let index = DatabaseSession.namedValue<int> "effect_index" value
                    let position = DatabaseSession.admitted (EffectPosition.create sequence) index
                    row value, position)

            return
                {
                    Items = items
                    Next =
                        if rows.Length > query.Limit then
                            items |> List.tryLast |> Option.map snd
                        else
                            None
                }
        }

    let private projectionsFor connection parent token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT case_id,payment_id,accepting_sequence,effect_index,payment_date,paid_amount,removed FROM claimcore.case_payments WHERE ($1::uuid IS NULL OR case_id=$1) ORDER BY payment_id"
                    [| DatabaseSession.parameter (parent |> Option.map CaseId.value) |]
                    token

            return
                rows
                |> List.map (fun value ->
                    let parent =
                        DatabaseSession.namedValue<Guid> "case_id" value
                        |> DatabaseSession.admitted CaseId.create

                    let identity =
                        DatabaseSession.namedValue<Guid> "payment_id" value
                        |> DatabaseSession.admitted OperationId.create

                    let sequence =
                        DatabaseSession.namedValue<int64> "accepting_sequence" value
                        |> DatabaseSession.admitted Sequence.create

                    let index = DatabaseSession.namedValue<int> "effect_index" value
                    let position = DatabaseSession.admitted (EffectPosition.create sequence) index

                    let content =
                        match
                            DatabaseSession.namedOptional<DateOnly> "payment_date" value,
                            DatabaseSession.namedOptional<decimal> "paid_amount" value,
                            DatabaseSession.namedOptional<bool> "removed" value
                        with
                        | None, None, Some removed -> PaymentProjectionContent.Cleared removed
                        | Some _, Some _, Some removed ->
                            PaymentProjectionContent.Available(row value, removed)
                        | _ ->
                            raise (RecordCorruptException "Incomplete private payment projection.")

                    {
                        PaymentProjectionRow.CaseId = parent
                        PaymentId = identity
                        Position = position
                        Content = content
                    })
        }

    let projections connection token = projectionsFor connection None token

    let owned connection parent token =
        projectionsFor connection (Some parent) token

    let restore connection (payment: PaymentProjectionRow) token =
        task {
            let date, amount, removed =
                match payment.Content with
                | PaymentProjectionContent.Cleared removed -> None, None, Some removed
                | PaymentProjectionContent.Available(entry, removed) ->
                    Some(CalendarDate.value (PaymentEntry.date entry)),
                    Some(Amount.value (PaymentEntry.amount entry)),
                    Some removed

            let! changed =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.case_payments(case_id,payment_id,accepting_sequence,effect_index,payment_date,paid_amount,removed) VALUES($1,$2,$3,$4,$5::date,$6,$7)"
                    [|
                        box (CaseId.value payment.CaseId)
                        box (OperationId.value payment.PaymentId)
                        box (Sequence.value (EffectPosition.sequence payment.Position))
                        box (EffectPosition.index payment.Position)
                        DatabaseSession.parameter date
                        DatabaseSession.parameter amount
                        DatabaseSession.parameter removed
                    |]
                    token

            if changed <> 1 then
                raise (RecordCorruptException "Payment projection restoration failed.")
        }
