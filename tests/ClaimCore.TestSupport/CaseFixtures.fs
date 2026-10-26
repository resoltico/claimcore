module ClaimCore.TestSupport.CaseFixtures

open ClaimCore.Domain
open ClaimCore.RecordFormat
open Expecto

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

let date value = CalendarDate.parse value |> accepted
let country value = CountryCode.parse value |> accepted
let name value = PartyName.parse value |> accepted
let reference value = CaseReference.parse value |> accepted
let money amount currency = Money.parse amount currency |> accepted

let registration (input: RegistrationInput) : Registration =
    {
        IncidentDate = date input.IncidentDate
        IncidentNotificationDate = date input.IncidentNotificationDate
        IncidentCountry = country input.IncidentCountry
        ClaimantName = name input.ClaimantName
        InsurerName = name input.InsurerName
        ClaimedAmount = money input.ClaimedAmount input.ClaimedCurrency
    }

let decision (input: DecisionInput) : Decision =
    {
        PaymentDecisionDate = date input.PaymentDecisionDate
        PayableAmount = money input.PayableAmount input.PayableCurrency
    }

let payment (input: PaymentInput) : Payment =
    { PaymentDate = date input.PaymentDate }

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
