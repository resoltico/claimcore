module ClaimCore.IntegrationTests.PaymentPositionTests

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.Postgres
open Expecto

let check expected actual message =
    if expected <> actual then
        failwith message

let value =
    function
    | Ok value -> value
    | Error _ -> failwith "Authored value was refused."

let private actualPayments () =
    let database = PostgresFixture.create().GetAwaiter().GetResult()
    let fixture = database.Fixture
    let token = CancellationToken.None
    let awaitResult (pending: System.Threading.Tasks.Task<'a>) = pending.GetAwaiter().GetResult()

    let sql statement =
        use connection = database.OwnerSource.OpenConnection()
        DatabaseSession.execute connection statement [||] token |> awaitResult |> ignore

    let verified () =
        match Verification.verify database.OwnerSource None token |> awaitResult with
        | VerificationResult.Verified position -> position
        | VerificationResult.Diverged finding -> failwithf "Expected verified history: %A" finding

    try
        let actor =
            ClaimCore.TestSupport.StoreFixtures.caseWorker fixture
            |> fun pending -> pending.GetAwaiter().GetResult()

        let mutable revision = 0L
        let mutable parent = None
        let mutable first = None

        let execute identity expected act =
            let input =
                OperationInput.Case
                    {
                        OperationId = identity
                        Target = CaseSelector.Reference "FIN-SERVICE-51"
                        ExpectedRevision = expected
                        Act = act
                    }

            let reply =
                Service.execute actor input CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()

            input, reply

        let accepted identity act =
            let input, reply = execute identity revision act

            match reply.Outcome with
            | ExecuteResult.Accepted receipt ->
                match receipt.Subject with
                | ReceiptSubject.Case body ->
                    revision <- Revision.value body.Revision
                    parent <- Some body.CaseId
                    input, body
                | _ -> failwith "Expected case receipt."
            | _ -> failwithf "Expected accepted case operation, received %A" reply.Outcome

        let _, _ = accepted (Guid.CreateVersion7()) (CaseAct.Open Inputs.registration)

        let _, _ =
            accepted
                (Guid.CreateVersion7())
                (CaseAct.Decide(
                    Inputs.decisionWith
                        [
                            BusinessField.PayableAmount, "100"
                            BusinessField.PaymentDecisionDate, "2026-08-04"
                        ]
                        Inputs.decision
                ))

        for index in 1..51 do
            let input, body =
                accepted
                    (Guid.CreateVersion7())
                    (CaseAct.RecordPayment(
                        Inputs.paymentWith
                            [
                                BusinessField.PaidAmount, "1"
                                BusinessField.PaymentDate, "2026-08-05"
                            ]
                            Inputs.payment
                    ))

            match body.Snapshot with
            | SnapshotDisclosure.Disclosed {
                                               Payment = Some(PaymentDifference.Recorded entry)
                                           } when index = 1 -> first <- Some(input, entry)
            | _ -> ()

        let read () =
            match
                Service.get
                    actor
                    {
                        Target = CaseSelector.Id(parent |> Option.get)
                    }
                    CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()
            with
            | Outcome.Succeeded value -> value
            | other -> failwithf "Expected current case: %A" other

        let otherOperation = Guid.CreateVersion7()

        let other =
            Service.execute
                actor
                (OperationInput.Case
                    {
                        OperationId = otherOperation
                        Target = CaseSelector.Reference "FIN-SERVICE-OTHER"
                        ExpectedRevision = 0L
                        Act = CaseAct.Open Inputs.registration
                    })
                CancellationToken.None
            |> fun pending -> pending.GetAwaiter().GetResult()

        let otherId =
            match other.Outcome with
            | ExecuteResult.Accepted { Subject = ReceiptSubject.Case value } -> value.CaseId
            | _ -> failwith "Second authorized case required."

        let mutable cursor = None
        let mutable pages = 0
        let mutable entries = []
        let mutable running = true

        while running do
            let page =
                Service.payments
                    actor
                    {
                        CasePaymentsRequest.Target = CaseSelector.Id(parent |> Option.get)
                        AfterCursor = cursor
                        Limit = 20
                    }
                    CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()

            match page with
            | Outcome.Succeeded page ->
                pages <- pages + 1
                check (if pages < 3 then 20 else 11) page.Items.Length "Bounded page size mismatch."

                if pages = 1 then
                    check true page.NextCursor.IsSome "Expected continuation after first twenty."

                    let foreign =
                        Service.payments
                            actor
                            {
                                CasePaymentsRequest.Target = CaseSelector.Id otherId
                                AfterCursor = page.NextCursor
                                Limit = 20
                            }
                            CancellationToken.None
                        |> fun pending -> pending.GetAwaiter().GetResult()

                    match foreign with
                    | Outcome.Rejected Refusal.InvalidCursor -> ()
                    | _ -> failwith "Cursor crossed its case binding."

                    let unknown =
                        PrincipalKey.human "https://issuer.example/" "unregistered-payment-reader"
                        |> value

                    let denied = ClaimCore.TestSupport.StoreFixtures.core fixture unknown

                    let hidden =
                        Service.payments
                            denied
                            {
                                CasePaymentsRequest.Target = CaseSelector.Id(parent |> Option.get)
                                AfterCursor = Some "invalid-cursor"
                                Limit = 20
                            }
                            CancellationToken.None
                        |> fun pending -> pending.GetAwaiter().GetResult()

                    match hidden with
                    | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
                    | _ -> failwith "Cursor validation disclosed a protected case."

                entries <- entries @ page.Items
                cursor <- page.NextCursor
                running <- cursor.IsSome
            | _ -> failwith "Expected authorized bounded payment page."

        check 3 pages "Expected three bounded pages for fifty-one active entries."
        check 51 entries.Length "Paging omitted an active entry."

        check
            51
            (entries
             |> List.map (fun item -> PaymentEntry.id item.Entry)
             |> Set.ofList
             |> Set.count)
            "Paging repeated an entry."

        check
            true
            (entries
             |> List.pairwise
             |> List.forall (fun (left, right) -> left.Position > right.Position))
            "Stable effect ordering was not retained."

        check
            true
            (entries |> List.forall _.CanRemove)
            "Authorized active entries require remove handles."

        let current = read ()
        let financial = CaseState.fields current.Snapshot.State |> Option.get |> _.Finance
        check 51L financial.PaymentCount "Fifty-one payments were not retained."
        check 51M (Amount.value financial.PaidAmount) "Paid total mismatch."
        check 3 current.LatestPayments.Length "Case latest slice is not bounded to three."
        let original, entry = first |> Option.get

        accepted
            (Guid.CreateVersion7())
            (CaseAct.RemovePayment(OperationId.value (PaymentEntry.id entry)))
        |> ignore

        let replay =
            Service.execute actor original CancellationToken.None
            |> fun pending -> pending.GetAwaiter().GetResult()

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | _ -> failwith "Removed payment identity was not recognized as accepted."

        let after =
            read () |> _.Snapshot.State |> CaseState.fields |> Option.get |> _.Finance

        check 50L after.PaymentCount "Replay recreated a removed payment."
        check 50M (Amount.value after.PaidAmount) "Removal aggregate mismatch."

        let checkpoint = verified ()

        RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.On token
        |> awaitResult
        |> ignore

        let rows () =
            use connection = database.OwnerSource.OpenConnection()

            DatabaseSession.rows
                connection
                "SELECT row_to_json(t)::text FROM claimcore.case_payments t ORDER BY payment_id"
                [||]
                token
            |> awaitResult

        let originalRows = rows ()

        for damage in
            [
                "UPDATE claimcore.case_payments SET paid_amount=paid_amount+1"
                "UPDATE claimcore.case_payments SET payment_date=payment_date+1"
                "UPDATE claimcore.case_payments SET removed=NOT removed"
                "UPDATE claimcore.case_payments SET accepting_sequence=1,effect_index=0 WHERE payment_id=(SELECT payment_id FROM claimcore.case_payments ORDER BY payment_id LIMIT 1)"
                "UPDATE claimcore.case_payments SET case_id=(SELECT case_id FROM claimcore.cases WHERE case_reference='FIN-SERVICE-OTHER')"
                "DELETE FROM claimcore.case_payments"
            ] do
            sql damage

            let recovered = Service.execute actor original token |> awaitResult

            match recovered.Outcome with
            | ExecuteResult.AlreadyAccepted _ -> ()
            | _ -> failwith "Current payment corruption or maintenance blocked exact replay."

            match Verification.verify database.OwnerSource None token |> awaitResult with
            | VerificationResult.Diverged finding ->
                check
                    VerificationFinding.CasePaymentsProjection
                    finding.Finding
                    "Wrong payment corruption finding."
            | _ -> failwith "Payment projection damage was not detected."

            ProjectionRebuild.rebuild
                database.OwnerSource
                (InstallationCheckpoint.create fixture.InstallationId checkpoint None
                 |> Option.get)
                token
            |> awaitResult
            |> value
            |> ignore

            check
                originalRows
                (rows ())
                "Rebuild changed original accepting positions or private payment facts."

            check checkpoint (verified ()) "Projection repair changed the ledger checkpoint."

        RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.Off token
        |> awaitResult
        |> ignore

        StoreFixtures.grant
            actor
            (Guid.CreateVersion7())
            fixture.Owner
            {
                Role = Role.DataSteward
                Scope = GrantTarget.Installation
            }
            true
        |> awaitResult
        |> fun reply ->
            match reply.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | _ -> failwith "Data steward grant refused."

        accepted (Guid.CreateVersion7()) (CaseAct.Bare BareAct.Erase) |> ignore
        let erasedCheckpoint = verified ()
        let erasedRows = rows ()
        use connection = database.OwnerSource.OpenConnection()

        let privateRows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.case_payments WHERE payment_date IS NOT NULL OR paid_amount IS NOT NULL"
                [||]
                token
            |> awaitResult

        check
            0L
            (DatabaseSession.value<int64> 0 privateRows.Head)
            "Erasure retained private payment facts."

        check 51 erasedRows.Length "Erasure discarded accepted payment identities."

        let removedMarkers =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.case_payments WHERE removed"
                [||]
                token
            |> awaitResult

        check
            1L
            (DatabaseSession.value<int64> 0 removedMarkers.Head)
            "Erasure retains the exact accepted removal marker."


        RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.On token
        |> awaitResult
        |> ignore

        sql "DELETE FROM claimcore.case_payments"

        ProjectionRebuild.rebuild
            database.OwnerSource
            (InstallationCheckpoint.create fixture.InstallationId erasedCheckpoint None
             |> Option.get)
            token
        |> awaitResult
        |> value
        |> ignore

        check
            erasedRows
            (rows ())
            "Rebuild resurrected erased payment content or lost accepting identities."

        check erasedCheckpoint (verified ()) "Erased reconstruction changed accepted history."

        printfn
            "PASS: 51 payments; bounded pages; removed-ID replay; six corruptions detected and repaired; erased content stays erased."
    finally
        fixture.Dispose()

let private lockedBounds () =
    let database = PostgresFixture.create().GetAwaiter().GetResult()
    let fixture = database.Fixture
    let token = CancellationToken.None
    let awaitResult (pending: System.Threading.Tasks.Task<'a>) = pending.GetAwaiter().GetResult()

    try
        let actor = StoreFixtures.caseWorker fixture |> awaitResult
        let mutable revision = 0L
        let target = CaseSelector.Reference "PAYMENT-BOUNDS"

        let request identity act =
            OperationInput.Case
                {
                    OperationId = identity
                    Target = target
                    ExpectedRevision = revision
                    Act = act
                }

        let accept services identity act =
            let reply = Service.execute services (request identity act) token |> awaitResult

            match reply.Outcome with
            | ExecuteResult.Accepted {
                                         Subject = ReceiptSubject.Case receipt
                                     } -> revision <- Revision.value receipt.Revision
            | other -> failtestf "Expected accepted bounds witness: %A" other

        let payment day =
            CaseAct.RecordPayment(
                Inputs.paymentWith
                    [ BusinessField.PaymentDate, day; BusinessField.PaidAmount, "1" ]
                    Inputs.payment
            )

        accept actor (Guid.CreateVersion7()) (CaseAct.Open Inputs.registration)

        accept
            actor
            (Guid.CreateVersion7())
            (CaseAct.Decide(
                Inputs.decisionWith
                    [
                        BusinessField.PaymentDecisionDate, "2026-08-04"
                        BusinessField.PayableAmount, "100"
                    ]
                    Inputs.decision
            ))

        let mutable entries =
            [
                for day in [ "2026-08-05"; "2026-08-06"; "2026-08-06"; "2026-08-08" ] ->
                    Guid.CreateVersion7(), Inputs.date day
            ]

        for identity, date in entries do
            accept actor identity (payment (CalendarDate.format date))

        let rollbackCalendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = actor.Calendar.TimeZoneId
                member _.DateAt _ = DateOnly(2026, 8, 4)
            }

        let rolledBack =
            { actor with
                Calendar = rollbackCalendar
            }

        let refused =
            Service.execute
                rolledBack
                (request (Guid.CreateVersion7()) (payment "2026-08-08"))
                token
            |> awaitResult

        match refused.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                (InputIssues.toList issues
                 |> List.map (fun issue -> issue.Target, issue.Violation))
                [ InputTarget.Field BusinessField.PaymentDate, Violation.FutureDate ]
                "A new payment cannot reuse an accepted future date"
        | other -> failtestf "Expected calendar rollback refusal: %A" other

        let earlier = Guid.CreateVersion7(), Inputs.date "2026-08-04"
        accept rolledBack (fst earlier) (payment "2026-08-04")
        entries <- earlier :: entries

        let checkPosition () =
            let current = Service.get actor { Target = target } token |> awaitResult

            let finance =
                match current with
                | Outcome.Succeeded value ->
                    value.Snapshot.State |> CaseState.fields |> Option.get |> _.Finance
                | other -> failtestf "Expected bounds read: %A" other

            Expect.equal
                finance.PaymentCount
                (int64 entries.Length)
                "Locked removal changes count exactly once"

            Expect.equal
                (Amount.value finance.PaidAmount)
                (decimal entries.Length)
                "Exact independently tracked sum"

            let dates = entries |> List.map snd

            let bounds =
                if dates.IsEmpty then
                    None
                else
                    PaymentDateBounds.create (List.min dates) (List.max dates)

            Expect.equal
                finance.PaymentDates
                bounds
                "Sole extrema, ties and final absence match the accepted entry set"

        checkPosition ()
        let selected = [ fst earlier; fst (List.last entries) ]

        let pending =
            selected
            |> List.map (fun identity ->
                Service.execute
                    actor
                    (request (Guid.CreateVersion7()) (CaseAct.RemovePayment identity))
                    token)
            |> List.toArray

        let replies = System.Threading.Tasks.Task.WhenAll pending |> awaitResult
        let mutable acceptedCount = 0
        let mutable conflicts = 0

        for selected, reply in List.zip selected (Array.toList replies) do
            match reply.Outcome with
            | ExecuteResult.Accepted {
                                         Subject = ReceiptSubject.Case receipt
                                     } ->
                acceptedCount <- acceptedCount + 1
                revision <- Revision.value receipt.Revision
                entries <- entries |> List.filter (fun (identity, _) -> identity <> selected)
            | ExecuteResult.Rejected(Refusal.RevisionConflict _) -> conflicts <- conflicts + 1
            | other ->
                failtestf "Expected one accepted removal or current revision conflict: %A" other

        Expect.equal
            (acceptedCount, conflicts)
            (1, 1)
            "Concurrent removals cannot consume stale aggregate or exclusion bounds"

        checkPosition ()
        // Remove earliest entries first, including the tied pair, then the sole final entry.
        for identity, _ in entries |> List.sortBy snd do
            accept actor (Guid.CreateVersion7()) (CaseAct.RemovePayment identity)
            entries <- entries |> List.filter (fun (existing, _) -> existing <> identity)
            checkPosition ()

        match Verification.verify database.OwnerSource None token |> awaitResult with
        | VerificationResult.Verified _ -> ()
        | other ->
            failtestf
                "Accepted rollback and concurrent removal history must independently replay: %A"
                other
    finally
        fixture.Dispose()

[<Tests>]
let tests =
    testList
        "PostgreSQL financial positions"
        [
            testCase
                "real PostgreSQL payment paging, reserved identity, projection repair and erasure"
                actualPayments
            testCase
                "locked payment bounds survive ties calendar rollback and concurrent removals"
                lockedBounds
        ]
