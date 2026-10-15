module ClaimCore.Tests.CaseModelTests

open System
open Expecto
open ClaimCore.Domain

let private today = DateOnly(2026, 9, 7)

let private registration =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
        ClaimantName = "Synthetic Party"
        InsurerName = "Synthetic Insurer"
        ClaimedAmount = "1000"
        ClaimedCurrency = "EUR"
    }

let private request revision act =
    {
        OperationId = Guid.Parse "20000000-0000-4000-8000-000000000001"
        CaseReference = "Demo-1"
        ExpectedRevision = revision
        Act = act
    }

let private accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected synthetic acceptance"

let private fields status decided paid : CaseFields =
    {
        IncidentDate = registration.IncidentDate
        IncidentNotificationDate = registration.IncidentNotificationDate
        IncidentCountry = registration.IncidentCountry
        ClaimantName = registration.ClaimantName
        InsurerName = registration.InsurerName
        ClaimedAmount = "1000"
        ClaimedCurrency = "EUR"
        CaseReference = "Demo-1"
        Status = status
        PaymentDecisionDate = if decided then Some "2026-08-15" else None
        PayableAmount = if decided then Some "750" else None
        PayableCurrency = if decided then Some "EUR" else None
        PaymentDate = if paid then Some "2026-08-20" else None
    }

let private retained revision view =
    Case.ofAccepted
        {
            Reference = "Demo-1"
            Revision = revision
            View = view
        }
    |> accepted

let private live status decided paid =
    CaseView.Live(fields status decided paid, Disposition.Active)

let private acts =
    [
        CaseActKind.Open, CaseAct.Business(BusinessCommand.Open registration)
        CaseActKind.AmendRegistration,
        CaseAct.Business(
            BusinessCommand.AmendRegistration
                { registration with
                    ClaimantName = "Changed Party"
                }
        )
        CaseActKind.CorrectCase,
        CaseAct.Business(
            BusinessCommand.CorrectCase
                {
                    Registration =
                        RegistrationCorrection.Replace
                            { registration with
                                ClaimantName = "Changed Party"
                            }
                    Decision = DecisionCorrection.Keep
                    Payment = PaymentCorrection.Keep
                }
        )
        CaseActKind.Decide,
        CaseAct.Business(
            BusinessCommand.Decide
                {
                    PaymentDecisionDate = "2026-08-15"
                    PayableAmount = "750"
                    PayableCurrency = "EUR"
                }
        )
        CaseActKind.WithdrawDecision, CaseAct.Business BusinessCommand.WithdrawDecision
        CaseActKind.RecordPayment, CaseAct.Business(BusinessCommand.RecordPayment "2026-08-20")
        CaseActKind.ClearPayment, CaseAct.Business BusinessCommand.ClearPayment
        CaseActKind.Close, CaseAct.Business BusinessCommand.Close
        CaseActKind.Reopen, CaseAct.Business BusinessCommand.Reopen
        CaseActKind.Void, CaseAct.Stewardship(StewardshipAct.Void "Entry error")
        CaseActKind.Reinstate, CaseAct.Stewardship(StewardshipAct.Reinstate "Entry confirmed")
        CaseActKind.Erase, CaseAct.Stewardship StewardshipAct.Erase
    ]

let private stateExpectations =
    [
        "absent", None, [ "OPEN" ]
        "erased", Some CaseView.Erased, []
        "voided",
        Some(CaseView.Live(fields CaseStatus.Opened false false, Disposition.Voided)),
        [ "REINSTATE"; "ERASE" ]
        "open undecided",
        Some(live CaseStatus.Opened false false),
        [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE"; "VOID"; "ERASE" ]
        "open decided",
        Some(live CaseStatus.Opened true false),
        [
            "CORRECT_CASE"
            "DECIDE"
            "WITHDRAW_DECISION"
            "RECORD_PAYMENT"
            "CLOSE"
            "VOID"
            "ERASE"
        ]
        "open paid",
        Some(live CaseStatus.Opened true true),
        [ "CORRECT_CASE"; "CLEAR_PAYMENT"; "CLOSE"; "VOID"; "ERASE" ]
        "closed undecided",
        Some(live CaseStatus.Closed false false),
        [ "CORRECT_CASE"; "REOPEN"; "VOID"; "ERASE" ]
        "closed decided",
        Some(live CaseStatus.Closed true false),
        [ "CORRECT_CASE"; "REOPEN"; "VOID"; "ERASE" ]
        "closed paid",
        Some(live CaseStatus.Closed true true),
        [ "CORRECT_CASE"; "REOPEN"; "VOID"; "ERASE" ]
    ]

let private matrix =
    testCase "independent state table governs all twelve acts and advertisements" (fun () ->
        Expect.equal acts.Length 12 "Complete act vocabulary"

        for _, view, expected in stateExpectations do
            let current = view |> Option.map (retained 1L)
            let revision = if view.IsSome then 1L else 0L

            for kind, act in acts do
                let allowed = List.contains (CaseActKinds.token kind) expected
                let result = Case.decide today (request revision act) current
                Expect.equal (Result.isOk result) allowed "Independent transition expectation"

            match current with
            | None -> ()
            | Some value ->
                let actual = Case.eligibleActs value |> List.map CaseActKinds.token |> Set.ofList

                Expect.isTrue
                    (actual = Set.ofList expected)
                    "Independent advertisement expectation")

let private grandfathering =
    testCase
        "retained nonconforming text renders and reaffirms while changed assertions refuse"
        (fun () ->
            let oldName = "Synthetic\u202eParty"

            let storedFields =
                { fields CaseStatus.Opened false false with
                    ClaimantName = oldName
                }

            let current = retained 1L (CaseView.Live(storedFields, Disposition.Active))

            Expect.isTrue
                ((Case.snapshot current).View = CaseView.Live(storedFields, Disposition.Active))
                "Retained fields are exact"

            let reaffirm =
                CaseAct.Business(
                    BusinessCommand.AmendRegistration
                        { registration with
                            ClaimantName = oldName
                        }
                )

            Expect.isOk
                (Case.decide today (request 1L reaffirm) (Some current))
                "Unchanged policy violation is grandfathered"

            let changed =
                CaseAct.Business(
                    BusinessCommand.AmendRegistration
                        { registration with
                            ClaimantName = oldName + "x"
                        }
                )

            Expect.isError
                (Case.decide today (request 1L changed) (Some current))
                "Changed assertion follows current policy"

            Expect.isOk
                (Case.validateRequest (request 1L changed))
                "Request format does not preempt state-aware admission")

let private revisionBounds =
    testCase "M minus one advances to terminal M and all fresh acts refuse there" (fun () ->
        let prior = retained (Revision.Maximum - 1L) (live CaseStatus.Opened false false)

        let next =
            Case.decide
                today
                (request (Revision.Maximum - 1L) (CaseAct.Business BusinessCommand.Close))
                (Some prior)
            |> accepted

        Expect.equal
            (Case.snapshot next).Revision
            Revision.Maximum
            "Last successor is representable"

        Expect.isEmpty (Case.eligibleActs next) "Terminal advice"

        for _, act in acts do
            Expect.isError
                (Case.decide today (request Revision.Maximum act) (Some next))
                "Terminal state refuses all fresh acts"

        for value in [ -1L; Revision.Maximum + 1L; Int64.MaxValue ] do
            Expect.isError
                (Case.validateRequest (request value (CaseAct.Stewardship StewardshipAct.Erase)))
                "Expected revision bounds"

        Expect.isOk
            (Case.validateRequest (
                request Revision.Maximum (CaseAct.Stewardship StewardshipAct.Erase)
            ))
            "Expected M is valid syntax")

let private identifiers =
    testCase
        "ASCII references fold identity and retain initial spelling; countries are syntax only"
        (fun () ->
            for reference in [ "!"; "~"; String.replicate 80 "a"; "<a>&'" ] do
                Expect.isOk (CaseReference.validate reference) "Printable ASCII profile"

            for reference in [ ""; "a b"; "Rīga"; String.replicate 81 "a"; "a\n" ] do
                Expect.isError (CaseReference.validate reference) "Reference format refusal"

            Expect.equal (CaseReference.key "i-Demo") "I-DEMO" "ASCII folding"
            let current = retained 1L (live CaseStatus.Opened false false)

            let recased =
                { request 1L (CaseAct.Business BusinessCommand.Close) with
                    CaseReference = "DEMO-1"
                }

            let next = Case.decide today recased (Some current) |> accepted |> Case.snapshot
            Expect.equal next.Reference "Demo-1" "Initial spelling remains authoritative"

            for country in [ "LV"; "ZZ"; "QQ" ] do
                let act =
                    CaseAct.Business(
                        BusinessCommand.Open
                            { registration with
                                IncidentCountry = country
                            }
                    )

                Expect.isOk (Case.decide today (request 0L act) None) "Membership is unchecked"

            for country in [ "Latvia"; "lv"; "L"; "LV\n"; "ĀĀ" ] do
                let act =
                    CaseAct.Business(
                        BusinessCommand.Open
                            { registration with
                                IncidentCountry = country
                            }
                    )

                Expect.isError (Case.validateRequest (request 0L act)) "Country syntax refusal")

let private refusedScalars =
    [
        yield! [ 0..31 ]
        yield! [ 127..159 ]
        yield 0x00AD
        yield 0x061C
        yield! [ 0x115F..0x1160 ]
        yield! [ 0x17B4..0x17B5 ]
        yield 0x200B
        yield! [ 0x200E..0x200F ]
        yield! [ 0x2028..0x202E ]
        yield! [ 0x2060..0x2064 ]
        yield! [ 0x2066..0x2069 ]
        yield 0x3164
        yield 0xFEFF
        yield 0xFFA0
        yield! [ 0xFFF9..0xFFFB ]
        yield! [ 0xE0000..0xE007F ]
        yield! [ 0xFDD0..0xFDEF ]
        for plane in 0..16 do
            yield (plane <<< 16) + 0xFFFE
            yield (plane <<< 16) + 0xFFFF
    ]

let private textActs value =
    [
        CaseAct.Business(
            BusinessCommand.Open
                { registration with
                    ClaimantName = value
                }
        )
        CaseAct.Business(
            BusinessCommand.Open
                { registration with
                    InsurerName = value
                }
        )
    ]

let private checkText expected scalar =
    let value = "A" + Char.ConvertFromUtf32 scalar + "B"

    for act in textActs value do
        Expect.equal
            (Case.decide today (request 0L act) None |> Result.isOk)
            expected
            "Business text scalar policy"

    let current = retained 1L (live CaseStatus.Opened false false)
    let act = CaseAct.Stewardship(StewardshipAct.Void value)

    Expect.equal
        (Case.decide today (request 1L act) (Some current) |> Result.isOk)
        expected
        "Reasons use the same profile"

let private unicode =
    testCase
        "every explicitly refused scalar and every deliberately allowed selector retains its meaning"
        (fun () ->
            for scalar in refusedScalars do
                checkText false scalar

            let allowed =
                [
                    0x034F
                    0x180B
                    0x180C
                    0x180D
                    0x180E
                    0x180F
                    0x200C
                    0x200D
                    0xE000
                    0xF0000
                    0x100000
                ]

            for scalar in allowed @ [ 0xFE00..0xFE0F ] @ [ 0xE0100..0xE01EF ] do
                checkText true scalar)

[<Tests>]
let tests =
    testList "unified case record" [ matrix; grandfathering; revisionBounds; identifiers; unicode ]
