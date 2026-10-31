module ClaimCore.TestSupport.CaseFixtures

open ClaimCore.Domain
open ClaimCore.RecordFormat
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

let decide today input current =
    Case.parse input
    |> Result.bind (fun command -> Case.decide today command current)

let encode input = command input |> RequestRecord.encode

let canonical input =
    command input |> Case.canonical |> _.Authored

let act value =
    command
        {
            OperationId = System.Guid.Parse "10000000-0000-4000-8000-000000000001"
            CaseReference = "FIXTURE"
            ExpectedRevision = 0L
            Act = value
        }
    |> _.Act

/// Fixture construction is independent of the product's group admission function.
let progress (decision: Decision option) (payment: Payment option) =
    match decision, payment with
    | None, None -> PaymentProgress.Undecided
    | Some value, None -> PaymentProgress.Decided value
    | Some value, Some paid -> PaymentProgress.Paid(value, paid)
    | None, Some _ -> failtest "A fixture cannot represent payment without a decision."

let private identifier create value =
    create value
    |> Option.defaultWith (fun () -> failtest "Invalid typed fixture identity or position.")

let operationId value = identifier OperationId.create value
let actorId value = identifier ActorId.create value
let caseId value = identifier CaseId.create value
let revision value = identifier Revision.create value
let sequence value = identifier Sequence.create value
