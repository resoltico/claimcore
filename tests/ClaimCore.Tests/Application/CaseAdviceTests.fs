module ClaimCore.Tests.CaseAdviceTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport

let private id n =
    Guid.Parse(sprintf "d0000000-0000-4000-8000-%012d" n)

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "Expected admitted value: %A" error

let private kinds =
    [
        CaseActKind.Open
        CaseActKind.AmendRegistration
        CaseActKind.Correct
        CaseActKind.Decide
        CaseActKind.WithdrawDecision
        CaseActKind.RecordPayment
        CaseActKind.ClearPayment
        CaseActKind.Close
        CaseActKind.Reopen
        CaseActKind.Void
        CaseActKind.Reinstate
        CaseActKind.Erase
    ]

let private blocked =
    kinds
    |> List.map (fun act ->
        {
            Act = act
            Reason = Refusal.Access AccessRefusal.ResourceUnavailable
        })

[<Tests>]
let model =
    testCase "case advice requires a complete unambiguous unavailable-act explanation" (fun () ->
        Expect.isOk (CaseAdvice.create [] None [] blocked) "Literal complete inventory is valid"

        Expect.isError
            (CaseAdvice.create [] None [] (List.tail blocked))
            "A missing Open reason is detected"

        Expect.isError
            (CaseAdvice.create [] None [] (blocked @ [ List.head blocked ]))
            "Duplicate reasons are detected"

        Expect.isError
            (CaseAdvice.create [ CaseActKind.Void ] None [] blocked)
            "Offered acts cannot also be blocked"

        let invalid =
            { List.head blocked with
                Reason = Refusal.Case CaseRefusal.CorrectionNoChanges
            }

        Expect.isError
            (CaseAdvice.create [] None [] (invalid :: List.tail blocked))
            "A no-change result is not a state-availability explanation"

        let advice =
            CaseAdvice.create
                [ CaseActKind.Void ]
                None
                []
                (blocked |> List.filter (fun item -> item.Act <> CaseActKind.Void))
            |> accepted

        let maintenance = RegisterMaintenance.enter RegisterMaintenance.initial |> accepted
        let current = CaseAdvice.duringMaintenance maintenance advice
        Expect.isEmpty current.Acts "Maintenance offers no new writes"
        Expect.equal current.Blocked.Length 12 "Every unavailable act still has one reason"

        Expect.equal
            (current.Blocked
             |> List.find (fun item -> item.Act = CaseActKind.Void)
             |> _.Reason)
            Refusal.RegisterMaintenance
            "A previously permitted act gets the maintenance reason"

        Expect.equal
            (current.Blocked
             |> List.find (fun item -> item.Act = CaseActKind.Decide)
             |> _.Reason)
            (Refusal.Access AccessRefusal.ResourceUnavailable)
            "Permission denials retain non-disclosure precedence"

        let encoded = Codec.encode ResponseCodecs.caseAdvice current

        Expect.equal
            (Codec.decode Limits.MaximumResponseBytes ResponseCodecs.caseAdvice encoded)
            (Ok current)
            "The complete maintenance contract round trips")

[<Tests>]
let service =
    testCaseAsync
        "case advice reasons share execution permissions state and maintenance precedence"
        (async {
            let token = CancellationToken.None

            use memory =
                new MemoryStore.Store(id 1, DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))

            let principal name =
                PrincipalKey.human "https://issuer.example/" name
                |> Result.defaultWith (fun _ -> failtest "Principal")

            let owner, reader, payment, hidden =
                principal "owner", principal "reader", principal "payment", principal "hidden"

            let seed n principal roles =
                memory.SeedActor
                    {
                        ActorId = CaseFixtures.actorId (id n)
                        Principal = principal
                        Enabled = true
                        Profile = None
                    }
                    (roles |> List.map (fun role -> role, None))

            seed 2 owner [ Role.Owner; Role.CaseHandler; Role.PaymentRecorder ]
            seed 3 reader [ Role.CaseReader ]
            seed 4 payment [ Role.PaymentRecorder ]
            seed 5 hidden [ Role.Owner ]

            let calendar =
                { new IBusinessCalendar with
                    member _.TimeZoneId = "Etc/UTC"
                    member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
                }

            let core principal =
                Service.create "synthetic" (id 1) (memory :> IStore) calendar principal

            let write n principal revision act =
                Service.execute
                    (core principal)
                    (Inputs.caseOperation
                        {
                            OperationId = id n
                            CaseReference = "ADVICE-1"
                            ExpectedRevision = revision
                            Act = act
                        })
                    token

            let! opened =
                write 10 owner 0L (AuthoredCaseAct.Open Inputs.registration) |> Async.AwaitTask

            match opened.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | other -> failtestf "Open: %A" other

            let get principal =
                Service.get
                    (core principal)
                    {
                        Target = CaseSelector.Reference "ADVICE-1"
                    }
                    token

            let read =
                function
                | Outcome.Succeeded value -> value
                | other -> failtestf "Read: %A" other

            let reason act (advice: CaseAdvice) =
                advice.Blocked |> List.find (fun item -> item.Act = act) |> _.Reason

            let! current = get owner |> Async.AwaitTask
            let current = read current

            Expect.equal
                (reason CaseActKind.RecordPayment current.Advice)
                (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided))
                "An authorized payment needs a recorded decision"

            Expect.equal
                current.Advice.Recommended
                (Some CaseActKind.Decide)
                "The alternative is actually offered"

            Expect.equal
                (Set.union
                    (Set.ofList current.Advice.Acts)
                    (current.Advice.Blocked |> List.map _.Act |> Set.ofList))
                (Set.ofList kinds)
                "The actual producer covers the literal act inventory"

            let! refused =
                write 11 owner 1L (AuthoredCaseAct.RecordPayment Inputs.payment)
                |> Async.AwaitTask

            Expect.equal
                refused.Outcome
                (ExecuteResult.Rejected(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided)))
                "Execution agrees with the literal state reason"

            let! current = get reader |> Async.AwaitTask
            let current = read current

            Expect.equal
                (reason CaseActKind.RecordPayment current.Advice)
                (Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment))
                "A readable denied action reports capability before state"

            let! refused =
                write 12 reader 1L (AuthoredCaseAct.RecordPayment Inputs.payment)
                |> Async.AwaitTask

            Expect.equal
                refused.Outcome
                (ExecuteResult.Rejected(
                    Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment)
                ))
                "Execution enforces the same permission first"

            let! closed =
                write 13 owner 1L (AuthoredCaseAct.Bare BareAct.Close) |> Async.AwaitTask

            match closed.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | other -> failtestf "Close: %A" other

            let! current = get payment |> Async.AwaitTask

            Expect.equal
                (reason CaseActKind.Correct (read current).Advice)
                (Refusal.Access(AccessRefusal.NotAuthorized Capability.Register))
                "A payment-only actor cannot correct a closed case with no financial group"

            let! entered = memory.ChangeMaintenance MaintenanceChange.Enter |> Async.AwaitTask
            accepted entered |> ignore
            let! current = get owner |> Async.AwaitTask

            Expect.equal
                (reason CaseActKind.Reopen (read current).Advice)
                Refusal.RegisterMaintenance
                "Maintenance blocks otherwise permitted reopening"

            Expect.equal
                (reason CaseActKind.Void (read current).Advice)
                (Refusal.Access(AccessRefusal.NotAuthorized Capability.Steward))
                "Maintenance does not replace a missing permission"

            let! unavailable = get hidden |> Async.AwaitTask

            Expect.equal
                unavailable
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Unreadable cases never expose advice or state"
        })
