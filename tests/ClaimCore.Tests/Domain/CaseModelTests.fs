module ClaimCore.Tests.CaseModelTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain

let private today = DateOnly(2026, 9, 7)

let private registration =
    { Inputs.registration with
        ClaimedAmount = "1000"
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
        CaseReference = CaseFixtures.reference "Demo-1"
        Status = status
        Registration =
            CaseFixtures.registration
                { registration with
                    ClaimedAmount = "1000"
                }
        Decision =
            if decided then
                Some(
                    CaseFixtures.decision
                        {
                            PaymentDecisionDate = "2026-08-15"
                            PayableAmount = "750"
                            PayableCurrency = "EUR"
                        }
                )
            else
                None
        Payment =
            if paid then
                Some
                    {
                        PaymentDate = CaseFixtures.date "2026-08-20"
                    }
            else
                None
    }

let private retained revision view =
    Case.ofAccepted
        {
            Reference = CaseFixtures.reference "Demo-1"
            Revision = revision
            State = view
        }
    |> accepted

let private live status decided paid =
    CaseState.Active(fields status decided paid)

let private acts =
    [
        CaseActKind.Open, (UnvalidatedCaseAct.Open registration)
        CaseActKind.AmendRegistration,
        (UnvalidatedCaseAct.AmendRegistration
            { registration with
                ClaimantName = "Changed Party"
            })
        CaseActKind.Correct,
        (UnvalidatedCaseAct.Correct
            {
                Registration =
                    RegistrationGroup.Replace
                        { registration with
                            ClaimantName = "Changed Party"
                        }
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            })
        CaseActKind.Decide,
        (UnvalidatedCaseAct.Decide
            {
                PaymentDecisionDate = "2026-08-15"
                PayableAmount = "750"
                PayableCurrency = "EUR"
            })
        CaseActKind.WithdrawDecision, UnvalidatedCaseAct.WithdrawDecision
        CaseActKind.RecordPayment, (UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" })
        CaseActKind.ClearPayment, UnvalidatedCaseAct.ClearPayment
        CaseActKind.Close, UnvalidatedCaseAct.Close
        CaseActKind.Reopen, UnvalidatedCaseAct.Reopen
        CaseActKind.Void, (UnvalidatedCaseAct.Void "Entry error")
        CaseActKind.Reinstate, (UnvalidatedCaseAct.Reinstate "Entry confirmed")
        CaseActKind.Erase, UnvalidatedCaseAct.Erase
    ]

let private stateExpectations =
    [
        "absent", None, [ "OPEN" ]
        "erased", Some CaseState.Erased, []
        "voided",
        Some(CaseState.Voided(fields CaseStatus.Opened false false)),
        [ "REINSTATE"; "ERASE" ]
        "open undecided",
        Some(live CaseStatus.Opened false false),
        [ "AMEND_REGISTRATION"; "DECIDE"; "CLOSE"; "VOID"; "ERASE" ]
        "open decided",
        Some(live CaseStatus.Opened true false),
        [
            "CORRECT"
            "DECIDE"
            "WITHDRAW_DECISION"
            "RECORD_PAYMENT"
            "CLOSE"
            "VOID"
            "ERASE"
        ]
        "open paid",
        Some(live CaseStatus.Opened true true),
        [ "CORRECT"; "CLEAR_PAYMENT"; "CLOSE"; "VOID"; "ERASE" ]
        "closed undecided",
        Some(live CaseStatus.Closed false false),
        [ "CORRECT"; "REOPEN"; "VOID"; "ERASE" ]
        "closed decided",
        Some(live CaseStatus.Closed true false),
        [ "CORRECT"; "REOPEN"; "VOID"; "ERASE" ]
        "closed paid",
        Some(live CaseStatus.Closed true true),
        [ "CORRECT"; "REOPEN"; "VOID"; "ERASE" ]
    ]

let private matrix =
    testCase "independent state table governs all twelve acts and advertisements" (fun () ->
        Expect.equal acts.Length 12 "Complete act vocabulary"

        for _, view, expected in stateExpectations do
            let current = view |> Option.map (retained 1L)
            let revision = if view.IsSome then 1L else 0L

            for kind, act in acts do
                let allowed = List.contains (CaseActKinds.token kind) expected
                let result = CaseFixtures.decide today (request revision act) current
                Expect.equal (Result.isOk result) allowed "Independent transition expectation"

            match current with
            | None -> ()
            | Some value ->
                let actual = Case.eligibleActs value |> List.map CaseActKinds.token |> Set.ofList

                Expect.isTrue
                    (actual = Set.ofList expected)
                    "Independent advertisement expectation")

let private allKeep = UnvalidatedCaseAct.Correct Inputs.correction

let private allKeepStates =
    [
        "absent", None, 0L, Refusal.ResourceUnavailable
        "erased", Some(retained 1L CaseState.Erased), 1L, Refusal.CaseErased
        "stale",
        Some(retained 1L (live CaseStatus.Opened true false)),
        0L,
        Refusal.RevisionConflict 1L
        "voided",
        Some(retained 1L (CaseState.Voided(fields CaseStatus.Opened true false))),
        1L,
        Refusal.CaseVoided
        "ineligible",
        Some(retained 1L (live CaseStatus.Opened false false)),
        1L,
        Refusal.CorrectionRequiresExistingValue
        "eligible",
        Some(retained 1L (live CaseStatus.Opened true false)),
        1L,
        Refusal.CorrectionNoChanges
        "closed",
        Some(retained 1L (live CaseStatus.Closed false false)),
        1L,
        Refusal.CorrectionNoChanges
    ]

let private allKeepPrecedence =
    testCase "all-KEEP correction preserves authoritative refusal order" (fun () ->
        for name, current, revision, refusal in allKeepStates do
            Expect.equal
                (CaseFixtures.decide today (request revision allKeep) current)
                (Error refusal)
                name

        let mismatch =
            { request 1L allKeep with
                CaseReference = "OTHER"
            }

        Expect.throwsT<System.ArgumentException>
            (fun () ->
                CaseFixtures.decide
                    today
                    mismatch
                    (Some(retained 1L (live CaseStatus.Opened true false)))
                |> ignore)
            "The store must supply the authoritative target record")

let private grandfathering =
    testCase
        "retained nonconforming text renders and reaffirms while changed assertions refuse"
        (fun () ->
            let oldName = "Synthetic‮Party"

            let storedFields =
                { fields CaseStatus.Opened false false with
                    Registration =
                        { (fields CaseStatus.Opened false false).Registration with
                            ClaimantName = CaseFixtures.name oldName
                        }
                }

            let current = retained 1L (CaseState.Active(storedFields))

            Expect.isTrue
                ((Case.snapshot current).State = CaseState.Active(storedFields))
                "Retained fields are exact"

            let reaffirm =
                (UnvalidatedCaseAct.AmendRegistration
                    { registration with
                        ClaimantName = oldName
                    })

            Expect.isOk
                (CaseFixtures.decide today (request 1L reaffirm) (Some current))
                "Unchanged policy violation is grandfathered"

            let changed =
                (UnvalidatedCaseAct.AmendRegistration
                    { registration with
                        ClaimantName = oldName + "x"
                    })

            Expect.isError
                (CaseFixtures.decide today (request 1L changed) (Some current))
                "Changed assertion follows current policy"

            Expect.isOk
                (CaseFixtures.validate (request 1L changed))
                "Request format does not preempt state-aware admission")

let private revisionBounds =
    testCase "expected revisions retain their permanent exact-integer format bound" (fun () ->
        for value in [ -1L; Revision.Maximum + 1L; Int64.MaxValue ] do
            Expect.isError
                (CaseFixtures.validate (request value (UnvalidatedCaseAct.Erase)))
                "Expected revision bounds"

        Expect.isOk
            (CaseFixtures.validate (request Revision.Maximum (UnvalidatedCaseAct.Erase)))
            "Expected M is valid syntax")

let private identifiers =
    testCase
        "ASCII references fold identity and retain initial spelling; countries are syntax only"
        (fun () ->
            for reference in [ "!"; "~"; String.replicate 80 "a"; "<a>&'" ] do
                Expect.isOk
                    ((CaseReference.parse
                      >> Result.map ignore
                      >> Result.mapError (fun violation ->
                          Refusal.InvalidInput(
                              InputTarget.Field BusinessField.CaseReference,
                              violation
                          )))
                        reference)
                    "Printable ASCII profile"

            for reference in [ ""; "a b"; "Rīga"; String.replicate 81 "a"; "a\n" ] do
                Expect.isError
                    ((CaseReference.parse
                      >> Result.map ignore
                      >> Result.mapError (fun violation ->
                          Refusal.InvalidInput(
                              InputTarget.Field BusinessField.CaseReference,
                              violation
                          )))
                        reference)
                    "Reference format refusal"

            Expect.equal (CaseReference.key "i-Demo") "I-DEMO" "ASCII folding"
            let current = retained 1L (live CaseStatus.Opened false false)

            let recased =
                { request 1L (UnvalidatedCaseAct.Close) with
                    CaseReference = "DEMO-1"
                }

            let next =
                CaseFixtures.decide today recased (Some current) |> accepted |> Case.snapshot

            Expect.equal
                (CaseReference.format next.Reference)
                "Demo-1"
                "Initial spelling remains authoritative"

            for country in [ "LV"; "ZZ"; "QQ" ] do
                let act =
                    (UnvalidatedCaseAct.Open
                        { registration with
                            IncidentCountry = country
                        })

                Expect.isOk
                    (CaseFixtures.decide today (request 0L act) None)
                    "Membership is unchecked"

            for country in [ "Latvia"; "lv"; "L"; "LV\n"; "ĀĀ" ] do
                let act =
                    (UnvalidatedCaseAct.Open
                        { registration with
                            IncidentCountry = country
                        })

                Expect.isError (CaseFixtures.validate (request 0L act)) "Country syntax refusal")

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
        (UnvalidatedCaseAct.Open
            { registration with
                ClaimantName = value
            })
        (UnvalidatedCaseAct.Open
            { registration with
                InsurerName = value
            })
    ]

let private checkText expected scalar =
    let value = "A" + Char.ConvertFromUtf32 scalar + "B"

    for act in textActs value do
        Expect.equal
            (CaseFixtures.decide today (request 0L act) None |> Result.isOk)
            expected
            "Business text scalar policy"

    let current = retained 1L (live CaseStatus.Opened false false)
    let act = (UnvalidatedCaseAct.Void value)

    Expect.equal
        (CaseFixtures.decide today (request 1L act) (Some current) |> Result.isOk)
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
    testList
        "unified case record"
        [
            matrix
            allKeepPrecedence
            grandfathering
            revisionBounds
            identifiers
            unicode
        ]
