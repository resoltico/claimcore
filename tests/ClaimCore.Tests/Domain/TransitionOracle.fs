module ClaimCore.Tests.TransitionOracle

open System
open Expecto
open ClaimCore.Domain

// An independent, fixed business vocabulary for the sequence property, not a second general parser.
type Witness = { Task: string; Inputs: InputMap }

type Model =
    {
        Snapshot: CaseSnapshot
        Payment: OperationId option
        Sequence: Sequence
    }

let private required result =
    result |> Result.defaultWith (fun issue -> failtestf "Oracle fixture: %A" issue)

let private field field value =
    TaskInputId.Target(InputTarget.Field field), InputValue.Text value

let private inputs values =
    InputMap.create (Map.ofList values) Map.empty |> Option.get

let private registrationValues name =
    [
        BusinessField.IncidentDate, "2026-08-01"
        BusinessField.ReceivedDate, "2026-08-03"
        BusinessField.IncidentCountry, "LV"
        BusinessField.PolicyNumber, name
        BusinessField.InsurerClaimReference, "Example Alleged Insurer"
        BusinessField.ClaimedAmount, "1000"
        BusinessField.ClaimedCurrency, "EUR"
    ]

let private registration name =
    RegistrationInput.create (registrationValues name)
    |> required
    |> Registration.parse
    |> required

let private business values =
    values |> List.map (fun (name, value) -> field name value) |> inputs

let private witness task values =
    {
        Task = task
        Inputs = business values
    }

let origin =
    Guid.Parse "20000000-0000-7000-8000-000000000001"
    |> OperationId.create
    |> Option.get

let reference = CaseReference.parse "UNIT-001" |> required

let opening =
    witness
        "case.open"
        ((BusinessField.CaseReference, "UNIT-001")
         :: registrationValues "Example Claimant Ltd")

let decide amount =
    witness
        "case.decide"
        [
            BusinessField.PaymentDecisionDate, "2026-08-15"
            BusinessField.PayableAmount, amount
            BusinessField.PayableCurrency, "EUR"
        ]

let payment =
    witness
        "case.recordPayment"
        [
            BusinessField.PaymentDate, "2026-08-20"
            BusinessField.PaidAmount, "750"
            BusinessField.PaidCurrency, "EUR"
        ]

let bare task =
    { Task = task; Inputs = InputMap.empty }

let amendment =
    witness "case.amendRegistration" (registrationValues "Changed Party")

let correction =
    let values = (business (registrationValues "Changed Party")).Values

    {
        Task = "case.correct"
        Inputs =
            InputMap.create
                (values
                 |> Map.add
                     (TaskInputId.Target(InputTarget.Group RecordGroup.Registration))
                     (InputValue.Text "UPDATE")
                 |> Map.add
                     (TaskInputId.Target(InputTarget.Group RecordGroup.Decision))
                     (InputValue.Text "KEEP"))
                Map.empty
            |> Option.get
    }

let private reason task =
    {
        Task = task
        Inputs = inputs [ TaskInputId.Target InputTarget.Reason, InputValue.Text "Entry error" ]
    }

let samples =
    [
        opening
        amendment
        correction
        decide "750"
        bare "case.withdrawDecision"
        payment
        bare "case.removePayment"
        { payment with
            Task = "case.correctPayment"
        }
        {
            Task = "case.close"
            Inputs = business [ BusinessField.Outcome, "SETTLED" ]
        }
        reason "case.reopen"
        reason "case.void"
        reason "case.reinstate"
        bare "case.erase"
    ]

let initial =
    {
        Snapshot =
            {
                CaseId = CaseId.ofOperationId origin
                Reference = reference
                Revision = Revision.first
                State =
                    CaseState.Active
                        {
                            CaseReference = reference
                            Handling = Handling.Open
                            Registration = registration "Example Claimant Ltd"
                            Finance = FinancialPosition.undecided
                        }
            }
        Payment = None
        Sequence = Sequence.first
    }

let private text field (witness: Witness) =
    match Map.find (TaskInputId.Target(InputTarget.Field field)) witness.Inputs.Values with
    | InputValue.Text value -> value
    | _ -> failtest "The oracle uses textual fixed witnesses."

let private activeEligible (fields: CaseFields) witness =
    match witness.Task with
    | "case.close" -> true
    | "case.amendRegistration" -> fields.Decision.IsNone
    | "case.decide"
    | "case.recordPayment" -> true
    | "case.withdrawDecision" -> fields.Decision.IsSome
    | "case.removePayment"
    | "case.correctPayment" -> fields.Finance.PaymentCount > 0L
    | _ -> false

let private eligible (fields: CaseFields) witness =
    match witness.Task with
    | "case.open" -> false
    | "case.reopen" -> fields.Status = CaseStatus.Closed
    | "case.correct" -> fields.Status = CaseStatus.Closed || fields.Decision.IsSome
    | _ when fields.Status = CaseStatus.Closed -> false
    | _ -> activeEligible fields witness

let allows state witness =
    match state, witness.Task with
    | CaseState.Erased, _ -> false
    | CaseState.Active _, "case.void" -> true
    | CaseState.Voided _, "case.reinstate" -> true
    | (CaseState.Active _ | CaseState.Voided _), "case.erase" -> true
    | CaseState.Active fields, _ -> eligible fields witness
    | CaseState.Voided _, _ -> false

let offered state =
    let existing =
        samples
        |> List.choose (fun witness -> if allows state witness then Some witness.Task else None)
        |> Set.ofList

    match state with
    | CaseState.Active _ -> Set.add "case.changeReference" existing
    | _ -> existing

let private finance decision paid count =
    let currency = Currency.parse "EUR" |> required

    let totals =
        if count = 0L then
            Map.empty
        else
            Map.ofList
                [
                    currency,
                    {
                        CurrencyPaymentTotals.Paid = DecimalTotal.parse paid |> required
                        Returned = DecimalTotal.zero
                    }
                ]

    FinancialPosition.create decision totals count |> required

let private changed (fields: CaseFields) witness =
    match witness.Task with
    | "case.amendRegistration"
    | "case.correct" ->
        { fields with
            Registration = registration "Changed Party"
        }
    | "case.decide" ->
        let value =
            DecisionInput.create
                [
                    BusinessField.PaymentDecisionDate, "2026-08-15"
                    BusinessField.PayableAmount, text BusinessField.PayableAmount witness
                    BusinessField.PayableCurrency, "EUR"
                ]
            |> required
            |> Decision.parse
            |> required

        { fields with
            Finance = FinancialPosition.withDecision (Some value) fields.Finance
        }
    | "case.withdrawDecision" ->
        { fields with
            Finance = FinancialPosition.withDecision None fields.Finance
        }
    | "case.recordPayment"
    | "case.correctPayment" ->
        { fields with
            Finance = finance fields.Decision "750" 1L
        }
    | "case.removePayment" ->
        { fields with
            Finance = finance fields.Decision "0" 0L
        }
    | "case.close" ->
        { fields with
            Handling =
                Handling.Closed
                    {
                        Outcome = ClosureOutcome.Settled
                        Note = None
                    }
        }
    | "case.reopen" -> { fields with Handling = Handling.Open }
    | _ -> fields

let advance (before: Model) stale witness operation sequence =
    if stale || not (allows before.Snapshot.State witness) then
        None
    else
        let state =
            match before.Snapshot.State, witness.Task with
            | CaseState.Active fields, "case.void" -> CaseState.Voided fields
            | CaseState.Voided fields, "case.reinstate" -> CaseState.Active fields
            | _, "case.erase" -> CaseState.Erased
            | CaseState.Active fields, _ -> CaseState.Active(changed fields witness)
            | _ -> failtest "The independent eligibility table omitted a state."

        if witness.Task = "case.correct" && state = before.Snapshot.State then
            None
        else
            Some
                {
                    Snapshot =
                        { before.Snapshot with
                            Revision =
                                Revision.create (Revision.value before.Snapshot.Revision + 1L)
                                |> Option.get
                            State = state
                        }
                    Payment =
                        match witness.Task with
                        | "case.recordPayment"
                        | "case.correctPayment" -> Some operation
                        | "case.removePayment"
                        | "case.erase" -> None
                        | _ -> before.Payment
                    Sequence = sequence
                }

let matches expected (actual: Result<CaseSnapshot, 'error>) =
    match expected, actual with
    | Some model, Ok observed -> model.Snapshot = observed
    | None, Error _ -> true
    | _ -> false
