module ClaimCore.Tests.CaseAdviceTests

open ClaimCore.Domain.Aggregates
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "Expected admitted value: %A" error

let private kinds =
    [
        (TaskId.ofLiteral "case.open")
        (TaskId.ofLiteral "case.amendRegistration")
        (TaskId.ofLiteral "case.changeReference")
        (TaskId.ofLiteral "case.correct")
        (TaskId.ofLiteral "case.decide")
        (TaskId.ofLiteral "case.withdrawDecision")
        (TaskId.ofLiteral "case.recordPayment")
        (TaskId.ofLiteral "case.removePayment")
        (TaskId.ofLiteral "case.correctPayment")
        (TaskId.ofLiteral "case.close")
        (TaskId.ofLiteral "case.reopen")
        (TaskId.ofLiteral "case.void")
        (TaskId.ofLiteral "case.reinstate")
        (TaskId.ofLiteral "case.erase")
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
        Expect.isOk
            (CaseAdvice.create [] None HistoryAccess.None blocked)
            "Literal complete inventory is valid"

        Expect.isError
            (CaseAdvice.create [] None HistoryAccess.None (List.tail blocked))
            "A missing Open reason is detected"

        Expect.isError
            (CaseAdvice.create [] None HistoryAccess.None (blocked @ [ List.head blocked ]))
            "Duplicate reasons are detected"

        Expect.isError
            (CaseAdvice.create [ (TaskId.ofLiteral "case.void") ] None HistoryAccess.None blocked)
            "Offered acts cannot also be blocked"

        let invalid =
            { List.head blocked with
                Reason = Refusal.Case CaseRefusal.CorrectionNoChanges
            }

        Expect.isError
            (CaseAdvice.create [] None HistoryAccess.None (invalid :: List.tail blocked))
            "A no-change result is not a state-availability explanation"

        let advice =
            CaseAdvice.create
                [ (TaskId.ofLiteral "case.void") ]
                None
                HistoryAccess.None
                (blocked |> List.filter (fun item -> item.Act <> (TaskId.ofLiteral "case.void")))
            |> accepted

        let family = ClaimCore.Tests.TaskBuilders.family ()
        let taskId = TaskId.ofLiteral "case.void"
        let binding = ClaimCore.Tests.TaskBuilders.caseBinding family

        let task =
            match ClaimCore.Domain.Tasks.Registry.find taskId with
            | Some(ClaimCore.Domain.Tasks.Task.Family task) -> task
            | _ -> failtest "Expected void task."

        let offered =
            task.Availability
                ClaimCore.Tests.TaskBuilders.environment
                (Some(DecisionFacts.fromReplay family))
                binding

        let advice =
            advice
            |> CaseAdvice.withOffers [ TaskOffer.Available(taskId, binding, offered) ]

        let maintenance =
            RegisterMaintenance.create
                1L
                (Some(System.Guid.Parse "10000000-0000-4000-8000-000000000099"))

        let current = CaseAdvice.duringMaintenance maintenance advice
        Expect.isEmpty current.Acts "Maintenance offers no new writes"

        Expect.equal
            current.Blocked.Length
            kinds.Length
            "Every unavailable act still has one reason"

        Expect.equal
            (current.Blocked
             |> List.find (fun item -> item.Act = (TaskId.ofLiteral "case.void"))
             |> _.Reason)
            Refusal.RegisterMaintenance
            "A previously permitted act gets the maintenance reason"

        Expect.equal
            (current.Blocked
             |> List.find (fun item -> item.Act = (TaskId.ofLiteral "case.decide"))
             |> _.Reason)
            (Refusal.Access AccessRefusal.ResourceUnavailable)
            "Permission denials retain non-disclosure precedence"

        match current.Offers with
        | [ TaskOffer.Available(_, _, available) ] ->
            Expect.equal
                available.Blocked
                (Some Refusal.RegisterMaintenance)
                "Maintenance also blocks captured executable controls"
        | _ -> failtest "Expected the retained blocked control."

        let encoded = Codec.encode ResponseCodecs.caseAdvice current

        Expect.equal
            (Codec.decode Limits.MaximumResponseBytes ResponseCodecs.caseAdvice encoded)
            (Ok(CaseAdvice.withOffers [] current))
            "The complete maintenance DATA contract round trips")
