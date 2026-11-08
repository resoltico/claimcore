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

let private fields registration progress status : CaseFields =
    {
        CaseReference = reference
        Registration = Inputs.admittedRegistration registration
        Progress = progress
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
    fields registration0 PaymentProgress.Undecided CaseStatus.Opened

let private decided =
    fields registration0 (PaymentProgress.Decided d0) CaseStatus.Opened

let private paid =
    fields registration0 (PaymentProgress.Paid(d0, [ p0 ])) CaseStatus.Opened

let private closed = { paid with Status = CaseStatus.Closed }

let private cases =
    [
        "OPEN", None, AuthoredCaseAct.Open registration0, snapshot 1L (CaseState.Active opened)
        "AMEND_REGISTRATION",
        Some(snapshot 1L (CaseState.Active opened)),
        AuthoredCaseAct.AmendRegistration(Inputs.registrationUpdate registration1),
        snapshot
            2L
            (CaseState.Active(fields registration1 PaymentProgress.Undecided CaseStatus.Opened))
        "DECIDE",
        Some(snapshot 1L (CaseState.Active opened)),
        AuthoredCaseAct.Decide decision0,
        snapshot 2L (CaseState.Active decided)
        "WITHDRAW_DECISION",
        Some(snapshot 3L (CaseState.Active decided)),
        AuthoredCaseAct.Bare BareAct.WithdrawDecision,
        snapshot 4L (CaseState.Active opened)
        "RECORD_PAYMENT",
        Some(snapshot 3L (CaseState.Active decided)),
        AuthoredCaseAct.RecordPayment payment0,
        snapshot 4L (CaseState.Active paid)
        "REMOVE_PAYMENT",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.RemovePayment(Guid.Parse "70000000-0000-7000-8000-000000000002"),
        snapshot 5L (CaseState.Active decided)
        "CLOSE",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.Bare BareAct.Close,
        snapshot 5L (CaseState.Active closed)
        "REOPEN",
        Some(snapshot 5L (CaseState.Active closed)),
        AuthoredCaseAct.Bare BareAct.Reopen,
        snapshot 6L (CaseState.Active paid)
        "VOID",
        Some(snapshot 5L (CaseState.Active closed)),
        AuthoredCaseAct.Void "Synthetic replayed correction",
        snapshot 6L (CaseState.Voided closed)
        "REINSTATE",
        Some(snapshot 6L (CaseState.Voided closed)),
        AuthoredCaseAct.Reinstate "Synthetic replayed correction",
        snapshot 7L (CaseState.Active closed)
        "CORRECT",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.Correct
            {
                Registration = Amendment.Update(Inputs.registrationUpdate registration1)
                Decision = Change.Update(Inputs.decisionUpdate decision1)
            },
        snapshot
            5L
            (CaseState.Active(
                fields registration1 (PaymentProgress.PartlyPaid(d1, [ p0 ])) CaseStatus.Opened
            ))
        "ERASE",
        Some(snapshot 6L (CaseState.Voided closed)),
        AuthoredCaseAct.Bare BareAct.Erase,
        snapshot 7L CaseState.Erased
    ]

let private history (before: CaseSnapshot) : CaseHistory =
    {
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
                    Guid.Parse "70000000-0000-7000-8000-000000000002"
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
        | "OPEN"
        | "AMEND_REGISTRATION"
        | "CORRECT" ->
            { value with
                Status = CaseStatus.Closed
            }
        | "DECIDE" ->
            { value with
                Progress = PaymentProgress.Paid(d0, [ p0 ])
            }
        | "RECORD_PAYMENT" ->
            { value with
                Progress = PaymentProgress.PartlyPaid(d1, [ p0 ])
            }
        | "REMOVE_PAYMENT" ->
            { value with
                Progress = PaymentProgress.Decided d1
            }
        | _ ->
            { value with
                Registration = Inputs.admittedRegistration registration1
            }

    { expected with
        State = CaseState.map change expected.State
    }

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

                        Expect.equal
                            (Case.decideSnapshot (DateOnly(2026, 9, 7)) command before)
                            (Ok expected)
                            "Hand-authored complete expected state"

                        if token = "ERASE" then
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
                            |> Option.map (fun before -> Map.ofList[id, history before])
                            |> Option.defaultValue Map.empty

                        let entry: StoredEntry =
                            {
                                Effects = LedgerEffects.single
                                Sequence = CaseFixtures.sequence 100L
                                EntryHash = Array.zeroCreate 32
                                OperationId = command.OperationId
                                ActorId = CaseFixtures.actorId operation
                                RecordedAt = instant
                                Subject =
                                    EntryTarget.Case(
                                        id,
                                        expected.Revision,
                                        CaseAct.kind command.Act,
                                        DateOnly(2026, 9, 7)
                                    )
                                CreationReference = if before.IsNone then Some reference else None
                                PayloadCommitment = Array.zeroCreate 32
                                PayloadNonce = Some(Array.zeroCreate 32)
                                Payload = Some [| 1uy |]
                            }

                        let recorded = if token = "ERASE" then None else Some expected

                        Expect.isOk
                            (CaseReplay.apply
                                histories
                                entry
                                (VerifiedPayload.Case(command, recorded)))
                            "Replacement fold positive control"

                        let forged =
                            if token = "ERASE" then
                                before
                            else
                                let value = tamper token expected

                                Expect.isOk
                                    (Case.ofAccepted value)
                                    "The negative remains a valid whole state; its failure is causal"

                                Some value

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
                    (CaseState.Active(fields facts PaymentProgress.Undecided CaseStatus.Opened))

            let entry: StoredEntry =
                {
                    Effects = LedgerEffects.single
                    Sequence = Sequence.first
                    EntryHash = Array.zeroCreate 32
                    OperationId = command.OperationId
                    ActorId = CaseFixtures.actorId operation
                    RecordedAt = recordedAt
                    Subject = EntryTarget.Case(id, Revision.first, CaseActKind.Open, storedDate)
                    CreationReference = Some reference
                    PayloadCommitment = Array.zeroCreate 32
                    PayloadNonce = Some(Array.zeroCreate 32)
                    Payload = Some [| 1uy |]
                }

            CaseReplay.apply Map.empty entry (VerifiedPayload.Case(command, Some expected))

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

            let before =
                snapshot
                    3L
                    (CaseState.Active(
                        fields
                            registration0
                            (PaymentProgress.PartlyPaid(d0, [ entry "000000000001" "20" ]))
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

            let expected =
                snapshot
                    4L
                    (CaseState.Active(
                        fields
                            registration0
                            (PaymentProgress.Paid(
                                d0,
                                [ entry "000000000001" "20"; entry "000000000002" "30" ]
                            ))
                            CaseStatus.Opened
                    ))

            let forged =
                snapshot
                    4L
                    (CaseState.Active(
                        fields
                            registration0
                            (PaymentProgress.Paid(
                                d0,
                                [ entry "000000000001" "21"; entry "000000000002" "29" ]
                            ))
                            CaseStatus.Opened
                    ))

            Expect.isOk
                (Case.ofAccepted forged)
                "The forged snapshot still satisfies every structural financial invariant"

            let stored: StoredEntry =
                {
                    Effects = LedgerEffects.single
                    Sequence = CaseFixtures.sequence 100L
                    EntryHash = Array.zeroCreate 32
                    OperationId = command.OperationId
                    ActorId = CaseFixtures.actorId operation
                    RecordedAt = instant
                    Subject =
                        EntryTarget.Case(
                            id,
                            expected.Revision,
                            CaseActKind.RecordPayment,
                            DateOnly(2026, 9, 7)
                        )
                    CreationReference = None
                    PayloadCommitment = Array.zeroCreate 32
                    PayloadNonce = Some(Array.zeroCreate 32)
                    Payload = Some [| 1uy |]
                }

            let prior = Map.ofList[id, history before]

            Expect.isOk
                (CaseReplay.apply prior stored (VerifiedPayload.Case(command, Some expected)))
                "The exact recorded payment is the positive control"

            Expect.isError
                (CaseReplay.apply prior stored (VerifiedPayload.Case(command, Some forged)))
                "Equal totals cannot conceal entry-level tampering")
