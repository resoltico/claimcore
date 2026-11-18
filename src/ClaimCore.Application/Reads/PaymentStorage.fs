namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts

/// Owned payment rows and scoped disclosure reads; Domain alone updates financial facts.
module internal PaymentStorage =
    type PageQuery =
        {
            CaseId: CaseId
            Before: EffectPosition option
            Limit: int
        }

    let columns =
        "payment_id,payment_date,paid_amount,currency,direction,reference,payee_id"

    let private accept =
        function
        | Ok value -> value
        | Error _ -> raise (RecordCorruptException "Invalid projected payment entry or total.")

    let row value =
        let identity =
            DatabaseSession.namedValue<Guid> "payment_id" value
            |> DatabaseSession.admitted OperationId.create

        let date =
            DatabaseSession.namedValue<DateOnly> "payment_date" value |> CalendarDate.ofDate

        let amount =
            (DatabaseSession.namedValue<decimal> "paid_amount" value)
                .ToString("0.####", Globalization.CultureInfo.InvariantCulture)
            |> Amount.parse
            |> accept

        let currency =
            DatabaseSession.namedValue<string> "currency" value |> Currency.parse |> accept

        let direction =
            DatabaseSession.namedValue<string> "direction" value
            |> Vocabulary.tryParse PaymentDirection.vocabulary
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid payment direction."))

        let reference =
            DatabaseSession.namedOptional<string> "reference" value
            |> Option.map (Identifier.parse >> accept)

        let payee =
            DatabaseSession.namedOptional<Guid> "payee_id" value
            |> Option.map (DatabaseSession.admitted PartyId.create)

        PaymentEntry.create identity direction (Money.create amount currency) date reference payee
        |> accept

    let payees connection parent entries token =
        task {
            let ids = entries |> List.choose PaymentEntry.payee |> List.distinct

            if ids.IsEmpty then
                return []
            else
                let! rows =
                    DatabaseSession.namedRows
                        connection
                        "SELECT party_id,name,removed FROM claimcore.case_parties WHERE case_id=$1 AND party_id=ANY($2::uuid[]) ORDER BY party_id"
                        [|
                            box (CaseId.value parent)
                            box (ids |> List.map PartyId.value |> List.toArray)
                        |]
                        token

                if rows.Length <> ids.Length then
                    raise (RecordCorruptException "A payment payee has no case party.")

                return
                    rows
                    |> List.map (fun row ->
                        {
                            PaymentPayee.Id =
                                DatabaseSession.namedValue<Guid> "party_id" row
                                |> DatabaseSession.admitted PartyId.create
                            Name =
                                DatabaseSession.namedOptional<string> "name" row
                                |> Option.map (PartyName.parse >> accept)
                            Removed = DatabaseSession.namedValue<bool> "removed" row
                        })
        }

    let latestPaidDates connection cases token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT case_id,max(payment_date) AS payment_date FROM claimcore.case_payments WHERE case_id=ANY($1::uuid[]) AND NOT removed AND direction=$2 GROUP BY case_id"
                    [|
                        box (cases |> List.map CaseId.value |> List.toArray)
                        box (PaymentDirection.token PaymentDirection.Paid)
                    |]
                    token

            return
                rows
                |> List.map (fun row ->
                    (DatabaseSession.namedValue<Guid> "case_id" row
                     |> DatabaseSession.admitted CaseId.create),
                    (DatabaseSession.namedValue<DateOnly> "payment_date" row |> CalendarDate.ofDate))
                |> Map.ofList
        }

    let totals connection parent token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    "SELECT case_id,currency,paid::text AS paid,returned::text AS returned FROM claimcore.case_payment_totals WHERE ($1::uuid IS NULL OR case_id=$1)"
                    [| DatabaseSession.parameter (parent |> Option.map CaseId.value) |]
                    token

            return
                rows
                |> List.groupBy (fun row ->
                    DatabaseSession.namedValue<Guid> "case_id" row
                    |> DatabaseSession.admitted CaseId.create)
                |> List.map (fun (id, rows) ->
                    id,
                    (rows
                     |> List.map (fun row ->
                         (DatabaseSession.namedValue<string> "currency" row
                          |> Currency.parse
                          |> accept),
                         {
                             CurrencyPaymentTotals.Paid =
                                 DatabaseSession.namedValue<string> "paid" row
                                 |> DecimalTotal.parse
                                 |> accept
                             Returned =
                                 DatabaseSession.namedValue<string> "returned" row
                                 |> DecimalTotal.parse
                                 |> accept
                         })
                     |> Map.ofList))
                |> Map.ofList
        }

    let latest connection (parent, limit) token =
        task {
            if limit < 1 || limit > PaymentPaging.MaximumLimit then
                invalidArg (nameof limit) "Payment read limit is out of range."

            let! rows =
                DatabaseSession.namedRows
                    connection
                    $"SELECT {columns} FROM claimcore.case_payments WHERE case_id=$1 AND NOT removed AND payment_date IS NOT NULL ORDER BY accepting_sequence DESC,effect_index DESC LIMIT $2"
                    [| box (CaseId.value parent); box limit |]
                    token

            return rows |> List.map row
        }

    let page
        connection
        (query: PageQuery)
        token
        : System.Threading.Tasks.Task<
            ClaimCore.Application.Storage.Page<PaymentEntry * EffectPosition, EffectPosition>
           >
        =
        task {
            let beforeSequence =
                query.Before |> Option.map (EffectPosition.sequence >> Sequence.value)

            let beforeIndex = query.Before |> Option.map EffectPosition.index

            let! rows =
                DatabaseSession.namedRows
                    connection
                    $"SELECT {columns},accepting_sequence,effect_index FROM claimcore.case_payments WHERE case_id=$1 AND NOT removed AND payment_date IS NOT NULL AND ($2::bigint IS NULL OR (accepting_sequence,effect_index)<($2::bigint,$3::integer)) ORDER BY accepting_sequence DESC,effect_index DESC LIMIT $4"
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
                    $"SELECT case_id,{columns},accepting_sequence,effect_index,removed FROM claimcore.case_payments WHERE ($1::uuid IS NULL OR case_id=$1) ORDER BY payment_id"
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
            let entry, removed =
                match payment.Content with
                | PaymentProjectionContent.Cleared removed -> None, removed
                | PaymentProjectionContent.Available(entry, removed) -> Some entry, removed

            let optional = DatabaseSession.parameter

            let! changed =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.case_payments(case_id,payment_id,accepting_sequence,effect_index,payment_date,paid_amount,currency,direction,reference,payee_id,removed) VALUES($1,$2,$3,$4,$5::date,$6,$7,$8,$9,$10,$11) ON CONFLICT(payment_id) DO UPDATE SET payment_date=EXCLUDED.payment_date,paid_amount=EXCLUDED.paid_amount,currency=EXCLUDED.currency,direction=EXCLUDED.direction,reference=EXCLUDED.reference,payee_id=EXCLUDED.payee_id,removed=EXCLUDED.removed WHERE claimcore.case_payments.case_id=EXCLUDED.case_id AND claimcore.case_payments.accepting_sequence=EXCLUDED.accepting_sequence AND claimcore.case_payments.effect_index=EXCLUDED.effect_index"
                    [|
                        box (CaseId.value payment.CaseId)
                        box (OperationId.value payment.PaymentId)
                        box (Sequence.value (EffectPosition.sequence payment.Position))
                        box (EffectPosition.index payment.Position)
                        optional (entry |> Option.map (PaymentEntry.date >> CalendarDate.value))
                        optional (entry |> Option.map (PaymentEntry.amount >> Amount.value))
                        optional (entry |> Option.map (PaymentEntry.money >> Money.currency))
                        optional (
                            entry |> Option.map (PaymentEntry.direction >> PaymentDirection.token)
                        )
                        optional (
                            entry
                            |> Option.bind PaymentEntry.reference
                            |> Option.map Identifier.format
                        )
                        optional (
                            entry |> Option.bind PaymentEntry.payee |> Option.map PartyId.value
                        )
                        box removed
                    |]
                    token

            if changed <> 1 then
                raise (RecordCorruptException "Payment projection restoration failed.")
        }

    let updateTotals connection id totals token =
        task {
            let entries = Map.toList totals
            let currencies = entries |> List.map (fst >> Currency.format) |> List.toArray

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.case_payment_totals WHERE case_id=$1 AND NOT(currency=ANY($2::text[]))"
                    [| box (CaseId.value id); box currencies |]
                    token

            if not entries.IsEmpty then
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_payment_totals(case_id,currency,paid,returned) SELECT $1,currency,paid::numeric,returned::numeric FROM unnest($2::text[],$3::text[],$4::text[]) AS totals(currency,paid,returned) ON CONFLICT(case_id,currency) DO UPDATE SET paid=EXCLUDED.paid,returned=EXCLUDED.returned WHERE (case_payment_totals.paid,case_payment_totals.returned) IS DISTINCT FROM (EXCLUDED.paid,EXCLUDED.returned)"
                        [|
                            box (CaseId.value id)
                            box currencies
                            box (
                                entries
                                |> List.map (
                                    snd
                                    >> (fun (bucket: CurrencyPaymentTotals) -> bucket.Paid)
                                    >> DecimalTotal.format
                                )
                                |> List.toArray
                            )
                            box (
                                entries
                                |> List.map (
                                    snd
                                    >> (fun (bucket: CurrencyPaymentTotals) -> bucket.Returned)
                                    >> DecimalTotal.format
                                )
                                |> List.toArray
                            )
                        |]
                        token

                ()
        }
