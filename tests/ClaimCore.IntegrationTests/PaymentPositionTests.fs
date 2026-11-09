module ClaimCore.IntegrationTests.PaymentPositionTests

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open Expecto

let check expected actual message =
    if expected <> actual then
        failwith message

let value =
    function
    | Ok value -> value
    | Error _ -> failwith "Authored value was refused."

let private actualPayments () =
    let fixture =
        ClaimCore.TestSupport.PostgresFixture.fixture().GetAwaiter().GetResult()

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
            | _ -> failwith "Expected current case."

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

        printfn
            "PASS: 51 accepted payments; latest slice3; same-transaction removal; exact removed-ID replay does not reinsert."
    finally
        fixture.Dispose()

[<Tests>]
let tests =
    testCase
        "real PostgreSQL payments exceed fifty with bounded case reads and reserved removed identity"
        actualPayments
