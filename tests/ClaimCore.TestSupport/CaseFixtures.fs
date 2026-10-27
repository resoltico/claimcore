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
