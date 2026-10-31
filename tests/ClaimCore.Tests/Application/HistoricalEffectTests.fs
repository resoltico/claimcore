module ClaimCore.Tests.HistoricalEffectTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private accepted value =
    Result.defaultWith (fun _ -> failtest "Independent permanent fixture must be valid") value

let private operation = Guid.Parse "70000000-0000-4000-8000-000000000001"
let private id = CaseFixtures.caseId operation
let private reference = CaseFixtures.reference "PERMANENT-EFFECT"
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
    Inputs.paymentFrom [ BusinessField.PaymentDate, "2026-09-04" ]

let private payment1 =
    Inputs.paymentFrom [ BusinessField.PaymentDate, "2026-09-05" ]

let private d0, d1 =
    Inputs.admittedDecision decision0, Inputs.admittedDecision decision1

let private p0, p1 =
    Inputs.admittedPayment payment0, Inputs.admittedPayment payment1

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
    fields registration0 (PaymentProgress.Paid(d0, p0)) CaseStatus.Opened

let private closed = { paid with Status = CaseStatus.Closed }

let private cases =
    [
        "OPEN", None, AuthoredCaseAct.Open registration0, snapshot 1L (CaseState.Active opened)
        "AMEND_REGISTRATION",
        Some(snapshot 1L (CaseState.Active opened)),
        AuthoredCaseAct.AmendRegistration registration1,
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
        "CLEAR_PAYMENT",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.Bare BareAct.ClearPayment,
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
        AuthoredCaseAct.Void "Synthetic permanent correction",
        snapshot 6L (CaseState.Voided closed)
        "REINSTATE",
        Some(snapshot 6L (CaseState.Voided closed)),
        AuthoredCaseAct.Reinstate "Synthetic permanent correction",
        snapshot 7L (CaseState.Active closed)
        "CORRECT",
        Some(snapshot 4L (CaseState.Active paid)),
        AuthoredCaseAct.Correct
            {
                Registration = Amendment.Replace registration1
                Decision = Change.Replace decision1
                Payment = Change.Replace payment1
            },
        snapshot
            5L
            (CaseState.Active(fields registration1 (PaymentProgress.Paid(d1, p1)) CaseStatus.Opened))
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
                    Guid.Parse "70000000-0000-4000-8000-000000000002"
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
                Progress = PaymentProgress.Paid(d0, p0)
            }
        | "RECORD_PAYMENT" ->
            { value with
                Progress = PaymentProgress.Paid(d1, p0)
            }
        | "CLEAR_PAYMENT" ->
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
        "independent permanent effects"
        [
            testCase "the authored effect matrix covers every permanent case act" (fun () ->
                Expect.equal
                    (cases |> List.map (fun (token, _, _, _) -> token) |> Set.ofList)
                    (Inventory.rows "caseActs"
                     |> List.map (fun row -> row.GetProperty("token").GetString() |> nonNull)
                     |> Set.ofList)
                    "Closed independent act inventory")
            for token, before, act, expected in cases do
                testCase
                    (token + " preserves independent expected effects and rejects unrelated changes")
                    (fun () ->
                        let command = request before act

                        Expect.equal
                            (HistoricalCaseEffects.apply command before)
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
                                "Expected state is permanently representable"

                        let histories =
                            before
                            |> Option.map (fun before -> Map.ofList[id, history before])
                            |> Option.defaultValue Map.empty

                        let entry: StoredEntry =
                            {
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
                            (CaseFold.apply
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
                            CaseFold.apply histories entry (VerifiedPayload.Case(command, forged))
                        with
                        | Error value ->
                            Expect.equal
                                value.Finding
                                VerificationFinding.StateSequence
                                "Request/result causal disagreement"
                        | Ok _ -> failtest "A changed unrelated result was accepted")
        ]

[<Tests>]
let correctionMatrix =
    let keep = Amendment.Keep
    let replace = Amendment.Replace registration1

    let rows =
        [
            "KEEP/KEEP/KEEP", keep, Change.Keep, Change.Keep, None
            "KEEP/KEEP/REPLACE",
            keep,
            Change.Keep,
            Change.Replace payment1,
            Some(registration0, PaymentProgress.Paid(d0, p1))
            "KEEP/KEEP/CLEAR",
            keep,
            Change.Keep,
            Change.Clear,
            Some(registration0, PaymentProgress.Decided d0)
            "KEEP/REPLACE/KEEP", keep, Change.Replace decision1, Change.Keep, None
            "KEEP/REPLACE/REPLACE",
            keep,
            Change.Replace decision1,
            Change.Replace payment1,
            Some(registration0, PaymentProgress.Paid(d1, p1))
            "KEEP/REPLACE/CLEAR",
            keep,
            Change.Replace decision1,
            Change.Clear,
            Some(registration0, PaymentProgress.Decided d1)
            "KEEP/CLEAR/KEEP", keep, Change.Clear, Change.Keep, None
            "KEEP/CLEAR/REPLACE", keep, Change.Clear, Change.Replace payment1, None
            "KEEP/CLEAR/CLEAR",
            keep,
            Change.Clear,
            Change.Clear,
            Some(registration0, PaymentProgress.Undecided)
            "REPLACE/KEEP/KEEP",
            replace,
            Change.Keep,
            Change.Keep,
            Some(registration1, PaymentProgress.Paid(d0, p0))
            "REPLACE/KEEP/REPLACE",
            replace,
            Change.Keep,
            Change.Replace payment1,
            Some(registration1, PaymentProgress.Paid(d0, p1))
            "REPLACE/KEEP/CLEAR",
            replace,
            Change.Keep,
            Change.Clear,
            Some(registration1, PaymentProgress.Decided d0)
            "REPLACE/REPLACE/KEEP", replace, Change.Replace decision1, Change.Keep, None
            "REPLACE/REPLACE/REPLACE",
            replace,
            Change.Replace decision1,
            Change.Replace payment1,
            Some(registration1, PaymentProgress.Paid(d1, p1))
            "REPLACE/REPLACE/CLEAR",
            replace,
            Change.Replace decision1,
            Change.Clear,
            Some(registration1, PaymentProgress.Decided d1)
            "REPLACE/CLEAR/KEEP", replace, Change.Clear, Change.Keep, None
            "REPLACE/CLEAR/REPLACE", replace, Change.Clear, Change.Replace payment1, None
            "REPLACE/CLEAR/CLEAR",
            replace,
            Change.Clear,
            Change.Clear,
            Some(registration1, PaymentProgress.Undecided)
        ]

    testList
        "independent permanent correction matrix"
        [
            testCase "every keep replace clear choice has one literal expectation" (fun () ->
                Expect.equal rows.Length 18 "Two registration and three downstream choices")
            for name, registration, decision, payment, expected in rows do
                testCase name (fun () ->
                    let before = snapshot 4L (CaseState.Active paid)

                    let command =
                        request
                            (Some before)
                            (AuthoredCaseAct.Correct
                                {
                                    Registration = registration
                                    Decision = decision
                                    Payment = payment
                                })

                    let actual = HistoricalCaseEffects.apply command (Some before)

                    match expected with
                    | None ->
                        Expect.isError
                            actual
                            "No-op or unacknowledged invalidation is not a permanent acceptance"
                    | Some(registration, progress) ->
                        Expect.equal
                            actual
                            (Ok(
                                snapshot
                                    5L
                                    (CaseState.Active(
                                        fields registration progress CaseStatus.Opened
                                    ))
                            ))
                            "Literal complete-group retention and replacement")
        ]
