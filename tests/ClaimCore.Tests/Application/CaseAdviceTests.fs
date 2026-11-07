module ClaimCore.Tests.CaseAdviceTests

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
