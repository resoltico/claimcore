module ClaimCore.TestSupport.CaseFixtures

open ClaimCore.Domain
open Expecto

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

let date = DomainInputs.date
let country = DomainInputs.country
let name = DomainInputs.name
let reference = DomainInputs.reference
let money = DomainInputs.money
let registration = DomainInputs.admittedRegistration
let decision = DomainInputs.admittedDecision
let payment = DomainInputs.admittedPayment

/// Construct validated fixture inputs; behavior expectations stay with their owner tests.
let finance (decision: Decision option) (payments: PaymentEntry list) =
    FinancialPosition.rebuild payments
    |> accepted
    |> FinancialPosition.withDecision decision

let netPaid currency position =
    let currency = Currency.parse currency |> accepted
    FinancialPosition.netIn currency position |> DecimalTotal.format

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

/// Pure retained-transition fixtures exercise state rules; context admission has separate denied/current tests.
let recordedContext (date: System.DateOnly) =
    let principal =
        PrincipalKey.human "https://issuer.example/" "subject-fixture" |> accepted

    let actor =
        {
            Actor.ActorId = actorId (System.Guid.Parse "10000000-0000-4000-8000-000000000001")
            Principal = principal
            Enabled = true
            Profile = None
        }

    {
        DecisionContext.BusinessDate = Some date
        ObservedAt =
            System.DateTimeOffset(date.ToDateTime(System.TimeOnly.MinValue), System.TimeSpan.Zero)
        Principal = principal
        Access =
            AccessView.ofActor (
                Some
                    {
                        Actor = actor
                        Grants =
                            Role.all
                            |> List.map (fun role ->
                                {
                                    Role = role
                                    Scope = Scope.Installation
                                })
                    }
            )
        Maintenance = false
        Lifetime = DecisionLifetime.Recorded
    }
