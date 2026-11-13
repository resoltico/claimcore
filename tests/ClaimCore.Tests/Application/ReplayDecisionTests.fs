module ClaimCore.Tests.ReplayDecisionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private accepted value =
    Result.defaultWith (fun _ -> failtest "Independent replayed fixture must be valid") value

let private operation = Guid.Parse "70000000-0000-7000-8000-000000000001"
let private id = CaseFixtures.caseId operation
let private reference = CaseFixtures.reference "REPLAY-EFFECT"
let private instant = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)

let private registration name =
    Inputs.registrationWith
        [ BusinessField.ClaimantName, name ]
        (Inputs.registrationOn "2026-09-01" "2026-09-02")

let private registration0, registration1 =
    registration "Before", registration "After"

let private decision0 =
    Inputs.decisionFrom
        [
            BusinessField.PaymentDecisionDate, "2026-09-03"
            BusinessField.PayableAmount, "50"
            BusinessField.PayableCurrency, "EUR"
        ]

let private decision1 =
    Inputs.decisionFrom
        [
            BusinessField.PaymentDecisionDate, "2026-09-03"
            BusinessField.PayableAmount, "60"
            BusinessField.PayableCurrency, "EUR"
        ]

let private payment0 =
    Inputs.paymentFrom
        [
            BusinessField.PaymentDate, "2026-09-04"
            BusinessField.PaidAmount, "50"
            BusinessField.PaidCurrency, "EUR"
        ]

let private d0, d1 =
    Inputs.admittedDecision decision0, Inputs.admittedDecision decision1

let private p0 =
    CaseFixtures.paymentEntry (Guid.Parse "70000000-0000-7000-8000-000000000002") payment0

let private fields registration finance status : CaseFields =
    {
        CaseReference = reference
        Registration = Inputs.admittedRegistration registration
        Finance = finance
        Status = status
    }

let private snapshot revision state : CaseSnapshot =
    {
        CaseId = id
        Reference = reference
        Revision = CaseFixtures.revision revision
        State = state
    }

let private opened =
    fields registration0 FinancialPosition.undecided CaseStatus.Opened

let private decided =
    fields registration0 (CaseFixtures.finance (Some d0) []) CaseStatus.Opened

let private paid =
    fields registration0 (CaseFixtures.finance (Some d0) [ p0 ]) CaseStatus.Opened

let private closed = { paid with Status = CaseStatus.Closed }

let private cases =
    [
        "case.open", None, AuthoredCaseAct.Open registration0, snapshot 1L (CaseState.Active opened)
        "case.amendRegistration",
        Some(snapshot 1L (CaseState.Active opened)),
        AuthoredCaseAct.AmendRegistration(Inputs.registrationUpdate registration1),
        snapshot
            2L
            (CaseState.Active(fields registration1 FinancialPosition.undecided CaseStatus.Opened))
        "case.decide",
        Some(snapshot 1L (CaseState.Active opened)),
        AuthoredCaseAct.Decide decision0,
        snapshot 2L (CaseState.Active decided)
        "case.withdrawDecision",
        Some(snapshot 3L (CaseState.Active decided)),
        AuthoredCaseAct.Bare BareAct.WithdrawDecision,
        snapshot 4L (CaseState.Active opened)
        "case.recordPayment",
        Some(snapshot 3L (CaseState.Active decided)),
        AuthoredCaseAct.RecordPayment payment0,
        snapshot 4L (CaseState.Active paid)
        "case.removePayment",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.RemovePayment(Guid.Parse "70000000-0000-7000-8000-000000000002"),
        snapshot 5L (CaseState.Active decided)
        "case.close",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.Bare BareAct.Close,
        snapshot 5L (CaseState.Active closed)
        "case.reopen",
        Some(snapshot 5L (CaseState.Active closed)),
        AuthoredCaseAct.Bare BareAct.Reopen,
        snapshot 6L (CaseState.Active paid)
        "case.void",
        Some(snapshot 5L (CaseState.Active closed)),
        AuthoredCaseAct.Void "Synthetic replayed correction",
        snapshot 6L (CaseState.Voided closed)
        "case.reinstate",
        Some(snapshot 6L (CaseState.Voided closed)),
        AuthoredCaseAct.Reinstate "Synthetic replayed correction",
        snapshot 7L (CaseState.Active closed)
        "case.correct",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.Correct
            {
                Registration = Amendment.Update(Inputs.registrationUpdate registration1)
                Decision = Change.Update(Inputs.decisionUpdate decision1)
            },
        snapshot
            5L
            (CaseState.Active(
                fields registration1 (CaseFixtures.finance (Some d1) [ p0 ]) CaseStatus.Opened
            ))
        "case.erase",
        Some(snapshot 6L (CaseState.Voided closed)),
        AuthoredCaseAct.Bare BareAct.Erase,
        snapshot 7L CaseState.Erased
    ]

let private history (before: CaseSnapshot) (payments: PaymentEntry list) : CaseHistory =
    {
        PaymentProjections =
            payments
            |> List.map (fun payment ->
                let row =
                    {
                        PaymentProjectionRow.CaseId = before.CaseId
                        PaymentId = PaymentEntry.id payment
                        Position = EffectPosition.create Sequence.first 0 |> Option.get
                        Content = PaymentProjectionContent.Available(payment, false)
                    }

                PaymentEntry.id payment, row)
            |> Map.ofList
        Revision = before.Revision
        ChangedAt = instant
        Snapshot = Some before
        Reference = Some reference
        State = CaseState.map ignore before.State
        Status =
            CaseState.fields before.State
            |> Option.map _.Status
            |> Option.defaultValue CaseStatus.Opened
        MissingPayloadAt = None
        PresentPayloadAt = Some(Sequence.first)
        ErasedAt = None
    }

let private request (before: CaseSnapshot option) act =
    Case.parse
        {
            OperationId =
                if before.IsNone then
                    operation
                else
                    match act with
                    | CaseAct.RecordPayment _ -> Guid.Parse "70000000-0000-7000-8000-000000000002"
                    | _ -> Guid.Parse "70000000-0000-7000-8000-000000000003"
            CaseReference = CaseReference.format reference
            ExpectedRevision =
                before
                |> Option.map (fun (value: CaseSnapshot) -> Revision.value value.Revision)
                |> Option.defaultValue 0L
            Act = act
        }
    |> accepted

let private tamper token (expected: CaseSnapshot) =
    let change (value: CaseFields) =
        match token with
        | "case.open"
        | "case.amendRegistration"
        | "case.correct" ->
            { value with
                Status = CaseStatus.Closed
            }
        | "case.decide" ->
            { value with
                Finance = CaseFixtures.finance (Some d0) [ p0 ]
            }
        | "case.recordPayment" ->
            { value with
                Finance = CaseFixtures.finance (Some d1) [ p0 ]
            }
        | "case.removePayment" ->
            { value with
                Finance = CaseFixtures.finance (Some d1) []
            }
        | _ ->
            { value with
                Registration = Inputs.admittedRegistration registration1
            }

    { expected with
        State = CaseState.map change expected.State
    }

let private storedEffect
    sequence
    recordedAt
    businessDate
    (command: CaseCommand)
    (decision: CaseDecision)
    =
    let header =
        {
            OperationHeader.InstallationId = Guid.Parse "90000000-0000-4000-8000-000000000001"
            Sequence = sequence
            PreviousHash = EntryHash.zero
            OperationId = command.OperationId
            ActorId = CaseFixtures.actorId operation
            RecordedAt = recordedAt
            BusinessDate = Some businessDate
        }

    let selected =
        match command.Act with
        | CaseAct.RemovePayment id -> Some id
        | _ -> None

    let scope =
        CommandSkeleton.Case
            {
                CaseId = decision.After.CaseId
                Reference = decision.After.Reference
                ExpectedRevision = command.ExpectedRevision
                Act = CaseAct.kind command.Act
                SelectedPayment = selected
            }

    RetainedOperation.create
        header
        scope
        (OperationValues.Case
            {
                Command = command
                Decision = decision
            })
        (fun () -> Array.create 32 1uy)
    |> accepted
    |> StoredEffect.ofOperation
    |> List.head

[<Tests>]
let tests =
    testList
        "independent replayed effects"
        [
            testCase "the authored effect matrix covers every replayed case act" (fun () ->
                Expect.equal
                    (cases |> List.map (fun (token, _, _, _) -> token) |> Set.ofList)
                    (CaseActKind.all |> List.map CaseActKind.token |> Set.ofList)
                    "Closed independent act inventory")
            for token, before, act, expected in cases do
                testCase
                    (token + " preserves independent expected effects and rejects unrelated changes")
                    (fun () ->
                        let command = request before act

                        let context =
                            match act, before with
                            | CaseAct.RecordPayment _, Some _ -> Map.empty
                            | CaseAct.RemovePayment _, Some parent ->
                                (ClaimCore.TestSupport.CaseFixtures.paymentRows
                                    (parent.CaseId)
                                    (Option.toList (Some p0)))
                            | _ -> Map.empty

                        let payment =
                            match act with
                            | CaseAct.RecordPayment _ -> Some(PaymentDifference.Recorded p0)
                            | CaseAct.RemovePayment _ -> Some(PaymentDifference.Removed p0)
                            | _ -> None

                        let decision = { After = expected; Payment = payment }

                        Expect.equal
                            (Case.decideSnapshot (DateOnly(2026, 9, 7)) context command before)
                            (Ok decision)
                            "Hand-authored complete expected state"

                        if token = "case.erase" then
                            Expect.isEmpty
                                (CaseRecord.encodeSnapshot expected)
                                "Erasure persists no business snapshot bytes"

                            Expect.isOk
                                (Case.ofAccepted expected)
                                "Retained erased metadata is restorable"
                        else
                            Expect.equal
                                (CaseRecord.decodeSnapshot (CaseRecord.encodeSnapshot expected))
                                (Ok expected)
                                "Expected state is replayedly representable"

                        let histories =
                            before
                            |> Option.map (fun before ->
                                Map.ofList[id,
                                           history
                                               before
                                               (if
                                                    (before.State
                                                     |> CaseState.fields
                                                     |> Option.map _.Finance.PaymentCount
                                                     |> Option.defaultValue 0L)
                                                        >
                                                        0L
                                                then
                                                    [ p0 ]
                                                else
                                                    [])])
                            |> Option.defaultValue Map.empty

                        let entry =
                            storedEffect
                                (CaseFixtures.sequence 100L)
                                instant
                                (DateOnly(2026, 9, 7))
                                command
                                decision

                        let recorded = if token = "case.erase" then None else Some decision

                        Expect.isOk
                            (CaseReplay.apply
                                histories
                                entry
                                (VerifiedPayload.Case(command, recorded)))
                            "Replacement fold positive control"

                        let forged =
                            if token = "case.erase" then
                                before
                                |> Option.map (fun snapshot ->
                                    { After = snapshot; Payment = None })
                            else
                                let value = tamper token expected

                                Expect.isOk
                                    (Case.ofAccepted value)
                                    "The negative remains a valid whole state; its failure is causal"

                                Some { decision with After = value }

                        match
                            CaseReplay.apply
                                histories
                                entry
                                (VerifiedPayload.Case(command, forged))
                        with
                        | Error value ->
                            Expect.equal
                                value.Finding
                                VerificationFinding.StateSequence
                                "Request/result causal disagreement"
                        | Ok _ -> failtest "A changed unrelated result was accepted")
        ]

[<Tests>]
let recordedBusinessDate =
    testCase "replay uses stored business date when recorded time is after midnight" (fun () ->
        let storedDate = DateOnly(2026, 9, 7)
        let recordedAt = DateTimeOffset(2026, 9, 8, 0, 0, 1, TimeSpan.Zero)

        let check factDate =
            let facts = Inputs.registrationOn factDate factDate
            let command = request None (AuthoredCaseAct.Open facts)

            let expected =
                snapshot
                    1L
                    (CaseState.Active(fields facts FinancialPosition.undecided CaseStatus.Opened))

            let entry =
                storedEffect
                    Sequence.first
                    recordedAt
                    storedDate
                    command
                    { After = expected; Payment = None }

            CaseReplay.apply
                Map.empty
                entry
                (VerifiedPayload.Case(command, Some { After = expected; Payment = None }))

        Expect.isOk (check "2026-09-07") "A fact at the stored business date is admitted"

        Expect.equal
            (check "2026-09-08" |> Result.map ignore)
            (Error
                {
                    Sequence = Some Sequence.first
                    Finding = VerificationFinding.StateSequence
                })
            "Recorded time cannot admit a future assertion that the stored decision date refuses")

[<Tests>]
let paymentAmountTamper =
    testCase
        "semantic replay detects changed payment amounts even when their paid total is unchanged"
        (fun () ->
            let entry number amount =
                PaymentEntry.create
                    (Guid.Parse("70000000-0000-7000-8000-" + number)
                     |> OperationId.create
                     |> Option.get)
                    (CaseFixtures.date "2026-09-04")
                    (Amount.parse amount |> accepted)
                |> accepted

            let original = entry "000000000004" "20"

            let before =
                snapshot
                    3L
                    (CaseState.Active(
                        fields
                            registration0
                            (CaseFixtures.finance (Some d0) [ original ])
                            CaseStatus.Opened
                    ))

            let input =
                Inputs.paymentFrom
                    [
                        BusinessField.PaymentDate, "2026-09-04"
                        BusinessField.PaidAmount, "30"
                        BusinessField.PaidCurrency, "EUR"
                    ]

            let command = request (Some before) (CaseAct.RecordPayment input)

            let added = entry "000000000002" "30"

            let expected =
                {
                    After =
                        snapshot
                            4L
                            (CaseState.Active(
                                fields
                                    registration0
                                    (CaseFixtures.finance (Some d0) [ original; added ])
                                    CaseStatus.Opened
                            ))
                    Payment = Some(PaymentDifference.Recorded added)
                }

            let forged =
                { expected with
                    Payment = Some(PaymentDifference.Recorded(entry "000000000002" "29"))
                }

            Expect.isOk
                (Case.ofAccepted forged.After)
                "Aggregate state still satisfies permanent invariants"

            let stored =
                storedEffect
                    (CaseFixtures.sequence 100L)
                    instant
                    (DateOnly(2026, 9, 7))
                    command
                    expected

            let prior = Map.ofList[id, history before [ original ]]

            Expect.isOk
                (CaseReplay.apply prior stored (VerifiedPayload.Case(command, Some expected)))
                "The exact recorded payment is the positive control"

            Expect.isError
                (CaseReplay.apply prior stored (VerifiedPayload.Case(command, Some forged)))
                "Equal totals cannot conceal entry-level tampering")
