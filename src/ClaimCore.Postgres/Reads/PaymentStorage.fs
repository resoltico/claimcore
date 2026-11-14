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
                    "INSERT INTO claimcore.case_payments(case_id,payment_id,accepting_sequence,effect_index,payment_date,paid_amount,removed) VALUES($1,$2,$3,$4,$5::date,$6,$7) ON CONFLICT(payment_id) DO UPDATE SET payment_date=EXCLUDED.payment_date,paid_amount=EXCLUDED.paid_amount,removed=EXCLUDED.removed WHERE claimcore.case_payments.case_id=EXCLUDED.case_id AND claimcore.case_payments.accepting_sequence=EXCLUDED.accepting_sequence AND claimcore.case_payments.effect_index=EXCLUDED.effect_index"
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
