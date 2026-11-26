module ClaimCore.IntegrationTests.PaymentPositionTests

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.Application
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
        match
            Verification.verify database.OwnerSource database.MasterKey None token
            |> awaitResult
        with
        | VerificationResult.Verified position -> position
        | VerificationResult.Diverged finding -> failwithf "Expected verified history: %A" finding

    try
        let actor =
            ClaimCore.TestSupport.StoreFixtures.caseWorker fixture
            |> fun pending -> pending.GetAwaiter().GetResult()

        let mutable revision = 0L
        let mutable parent = None
        let mutable first = None

        let execute identity expected taskName values =
            let input =
                StoreFixtures.request fixture 0 "FIN-SERVICE-51" expected taskName values
                |> Submissions.withOperationId identity

            let reply =
                Service.executeSubmission actor input CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()

            input, reply

        let accepted identity taskName values =
            let input, reply = execute identity revision taskName values

            match reply.Outcome with
            | ExecuteResult.Accepted receipt ->
                match receipt.Subject with
                | ReceiptSubject.Case body ->
                    revision <- Revision.value body.Revision
                    parent <- Some body.CaseId
                    input, body
                | _ -> failwith "Expected case receipt."
            | _ -> failwithf "Expected accepted case operation, received %A" reply.Outcome

        let _, _ =
            accepted
                ((Guid.CreateVersion7()))
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (
                    RegistrationInput.values (DomainInputs.registration)
                ))

        let _, _ =
            accepted
                ((Guid.CreateVersion7()))
                "case.decide"
                (ClaimCore.TestSupport.Submissions.fields (
                    DecisionInput.values (
                        (DomainInputs.decisionWith
                            [
                                BusinessField.PayableAmount, "100"
                                BusinessField.PaymentDecisionDate, "2026-08-04"
                            ]
                            DomainInputs.decision)
                    )
                ))

        for index in 1..51 do
            let input, body =
                accepted
                    ((Guid.CreateVersion7()))
                    "case.recordPayment"
                    (ClaimCore.TestSupport.Submissions.fields (
                        PaymentInput.values (
                            (DomainInputs.paymentWith
                                [
                                    BusinessField.PaidAmount, "1"
                                    BusinessField.PaymentDate, "2026-08-05"
                                ]
                                DomainInputs.payment)
                        )
                    ))

            match body.Snapshot with
            | SnapshotDisclosure.Disclosed {
                                               Payments = [ PaymentDifference.Recorded entry ]
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
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw
                    "case.open"
                    (otherOperation)
                    (ClaimCore.Application.Contracts.OperationBinding.CaseNew(0L))
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (DomainInputs.registration)
                     )
                     |> Map.add
                         (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                         (ClaimCore.Application.Contracts.SubmissionValue.Text("FIN-SERVICE-OTHER")))))
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
            (entries
             |> List.forall (fun item ->
                 item.Actions =
                     [
                         TaskId.ofLiteral "case.removePayment"
                         TaskId.ofLiteral "case.correctPayment"
                     ]))
            "Each selected payment independently supplies both exact owned action identities."

        // The caller keeps ReadCase through its separate handler role, while both payment
        // capabilities are freshly checked after revocation rather than inferred from old controls.
        let paymentRole active =
            StoreFixtures.grant
                actor
                (Guid.CreateVersion7())
                fixture.Owner
                {
                    Role = Role.PaymentRecorder
                    Scope = GrantTarget.Installation
                }
                active
            |> awaitResult
            |> StoreFixtures.acceptedPeople

        paymentRole false

        match
            Service.payments
                actor
                {
                    CasePaymentsRequest.Target = CaseSelector.Id(parent |> Option.get)
                    AfterCursor = None
                    Limit = 1
                }
                token
            |> awaitResult
        with
        | Outcome.Succeeded page ->
            check 1 page.Items.Length "Read-only payment visibility remains available."

            check
                []
                page.Items.Head.Actions
                "Fresh authority supplies neither payment task after payment rights are revoked."
        | other -> failwithf "Expected read-only payment page: %A" other

        paymentRole true

        let current = read ()
        let financial = CaseState.fields current.Snapshot.State |> Option.get |> _.Finance
        check 51L financial.PaymentCount "Fifty-one payments were not retained."
        check "51" (CaseFixtures.netPaid "EUR" financial) "Paid total mismatch."

        check 3 current.LatestPayments.Length "Case latest slice is not bounded to three."

        let original, entry = first |> Option.get

        accepted
            ((Guid.CreateVersion7()))
            "case.removePayment"
            (Map.ofList
                [
                    TaskInputId.Target InputTarget.PaymentId,
                    ClaimCore.Application.Contracts.SubmissionValue.Text(
                        ((OperationId.value (PaymentEntry.id entry))).ToString("D")
                    )
                ])
        |> ignore

        let replay =
            Service.executeSubmission actor original CancellationToken.None
            |> fun pending -> pending.GetAwaiter().GetResult()

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | _ -> failwith "Removed payment identity was not recognized as accepted."

        let after =
            read () |> _.Snapshot.State |> CaseState.fields |> Option.get |> _.Finance

        check 50L after.PaymentCount "Replay recreated a removed payment."
        check "50" (CaseFixtures.netPaid "EUR" after) "Removal aggregate mismatch."

        let checkpoint = verified ()

        RegisterMaintenanceStore.change
            database.OwnerSource
            database.MasterKey
            (MaintenanceChange.Acquire(0L, System.Guid.Parse "10000000-0000-4000-8000-000000000099"))
            token
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

            let recovered = Service.executeSubmission actor original token |> awaitResult

            match recovered.Outcome with
            | ExecuteResult.AlreadyAccepted _ -> ()
            | _ -> failwith "Current payment corruption or maintenance blocked exact replay."

            match
                Verification.verify database.OwnerSource database.MasterKey None token
                |> awaitResult
            with
            | VerificationResult.Diverged finding ->
                check
                    VerificationFinding.CasePaymentsProjection
                    finding.Finding
                    "Wrong payment corruption finding."
            | _ -> failwith "Payment projection damage was not detected."

            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                (InstallationCheckpoint.create fixture.InstallationId checkpoint None
                 |> Option.get)
                (ClaimCore.Application.Contracts.MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
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

        RegisterMaintenanceStore.change
            database.OwnerSource
            database.MasterKey
            (MaintenanceChange.Resume(
                MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                |> Option.get
            ))
            token
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

        accepted ((Guid.CreateVersion7())) "case.erase" (Map.empty) |> ignore
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


        RegisterMaintenanceStore.change
            database.OwnerSource
            database.MasterKey
            (MaintenanceChange.Acquire(2L, System.Guid.Parse "10000000-0000-4000-8000-000000000099"))
            token
        |> awaitResult
        |> ignore

        sql "DELETE FROM claimcore.case_payments"

        ProjectionRebuild.rebuild
            database.OwnerSource
            database.MasterKey
            (InstallationCheckpoint.create fixture.InstallationId erasedCheckpoint None
             |> Option.get)
            (ClaimCore.Application.Contracts.MaintenanceHandle.create
                3L
                (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
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

        let request identity taskName values =
            StoreFixtures.request fixture 0 "PAYMENT-BOUNDS" revision taskName values
            |> Submissions.withOperationId identity

        let accept services identity taskName values =
            let reply =
                Service.executeSubmission services (request identity taskName values) token
                |> awaitResult

            match reply.Outcome with
            | ExecuteResult.Accepted {
                                         Subject = ReceiptSubject.Case receipt
                                     } -> revision <- Revision.value receipt.Revision
            | other -> failtestf "Expected accepted bounds witness: %A" other

        let payment day =
            DomainInputs.paymentWith
                [ BusinessField.PaymentDate, day; BusinessField.PaidAmount, "1" ]
                DomainInputs.payment
            |> PaymentInput.values
            |> Submissions.fields

        accept
            (actor)
            ((Guid.CreateVersion7()))
            "case.open"
            (ClaimCore.TestSupport.Submissions.fields (
                RegistrationInput.values (DomainInputs.registration)
            ))

        accept
            (actor)
            ((Guid.CreateVersion7()))
            "case.decide"
            (ClaimCore.TestSupport.Submissions.fields (
                DecisionInput.values (
                    (DomainInputs.decisionWith
                        [
                            BusinessField.PaymentDecisionDate, "2026-08-04"
                            BusinessField.PayableAmount, "100"
                        ]
                        DomainInputs.decision)
                )
            ))

        let mutable entries =
            [
                for day in [ "2026-08-05"; "2026-08-06"; "2026-08-06"; "2026-08-08" ] ->
                    Guid.CreateVersion7(), DomainInputs.date day
            ]

        for identity, date in entries do
            accept actor identity "case.recordPayment" (payment (CalendarDate.format date))

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
            Service.executeSubmission
                rolledBack
                (request (Guid.CreateVersion7()) "case.recordPayment" (payment "2026-08-08"))
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

        let earlier = Guid.CreateVersion7(), DomainInputs.date "2026-08-04"
        accept rolledBack (fst earlier) "case.recordPayment" (payment "2026-08-04")
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
                (CaseFixtures.netPaid "EUR" finance)
                (string entries.Length)
                "Exact independently tracked sum"

            let latest = entries |> List.map snd |> List.sortDescending |> List.tryHead

            let paidStep =
                match current with
                | Outcome.Succeeded value ->
                    match value.Stages with
                    | CaseStageDisplay.Tracker(_, stages) ->
                        stages
                        |> List.find (fun stage -> stage.Stage = CaseStageKind.Paid)
                        |> _.Date
                    | CaseStageDisplay.Erased -> None
                | other -> failtestf "Expected payment stage: %A" other

            Expect.equal paidStep latest "Latest paid date agrees with the projection after removal"

        checkPosition ()
        let selected = [ fst earlier; fst (List.last entries) ]

        let pending =
            selected
            |> List.map (fun identity ->
                Service.executeSubmission
                    actor
                    (request
                        ((Guid.CreateVersion7()))
                        "case.removePayment"
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.PaymentId,
                                ClaimCore.Application.Contracts.SubmissionValue.Text(
                                    (identity).ToString("D")
                                )
                            ]))
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
            accept
                (actor)
                ((Guid.CreateVersion7()))
                "case.removePayment"
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.PaymentId,
                        ClaimCore.Application.Contracts.SubmissionValue.Text(
                            (identity).ToString("D")
                        )
                    ])

            entries <- entries |> List.filter (fun (existing, _) -> existing <> identity)
            checkPosition ()

        match
            Verification.verify database.OwnerSource database.MasterKey None token
            |> awaitResult
        with
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
