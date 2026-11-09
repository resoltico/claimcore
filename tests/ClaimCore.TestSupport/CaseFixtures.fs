module ClaimCore.TestSupport.CaseFixtures

open ClaimCore.Domain
open Expecto

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

let date = Inputs.date
let country = Inputs.country
let name = Inputs.name
let reference = Inputs.reference
let money = Inputs.money
let registration = Inputs.admittedRegistration
let decision = Inputs.admittedDecision
let payment = Inputs.admittedPayment

let command input = Case.parse input |> accepted
let validate input = Case.parse input |> Result.map ignore

let decide today context input current =
    let snapshot = current |> Option.map Case.snapshot

    Case.admit today snapshot input
    |> Result.mapError CaseDecisionError.Refused
    |> Result.bind (fun command -> Case.decide today context command current)

let canonical input =
    command input |> Case.canonical |> _.Authored

let act value =
    command
        {
            OperationId = System.Guid.Parse "10000000-0000-7000-8000-000000000001"
            CaseReference = "FIXTURE"
            ExpectedRevision = 0L
            Act = value
        }
    |> _.Act

/// Construct validated fixture inputs; behavior expectations stay with their owner tests.
let finance (decision: Decision option) (payments: PaymentEntry list) =
    let paid = payments |> List.sumBy (PaymentEntry.amount >> Amount.value)

    let paid =
        Amount.parse (paid.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
        |> accepted

    let dates =
        match payments |> List.map PaymentEntry.date with
        | [] -> None
        | dates -> PaymentDateBounds.create (List.min dates) (List.max dates)

    FinancialPosition.create decision paid (int64 payments.Length) dates |> accepted

let private identifier create value =
    create value
    |> Option.defaultWith (fun () -> failtest "Invalid typed fixture identity or position.")

let operationId value = identifier OperationId.create value
let actorId value = identifier ActorId.create value
let caseId value = identifier CaseId.create value
let revision value = identifier Revision.create value
let sequence value = identifier Sequence.create value

let paymentEntry id input =
    PaymentEntry.recorded (operationId id) (payment input)
