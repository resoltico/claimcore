module ClaimCore.Tests.Fixtures

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain

let today = DateOnly(2026, 9, 7)

let internal businessContext effectiveBusinessDate =
    {
        ObservedUtcInstant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
        EffectiveBusinessDate = effectiveBusinessDate
        TimeZoneId = "Etc/UTC"
    }

let internal businessTime effectiveBusinessDate =
    { new IBusinessTime with
        member _.Capture() = businessContext effectiveBusinessDate
    }

let boundRequest (draft: CommandDraft) =
    Drafts.bind draft
    |> Result.defaultWith (fun _ -> failtest "Synthetic test draft must bind to a command request.")

let registration =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "Lithuania"
        ClaimantName = "Example Claimant Ltd"
        InsurerName = "Example Alleged Insurer"
        ClaimedAmount = "1000.00"
        ClaimedCurrency = "EUR"
    }

let decision =
    {
        PaymentDecisionDate = "2026-08-15"
        PayableAmount = "750.00"
        PayableCurrency = "EUR"
    }

let request version command =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000001")
        CaseReference = "UNIT-001"
        ExpectedVersion = version
        Command = command
    }

let accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected acceptance."

let opened () =
    Claim.decide today (request 0L (Command.Open registration)) None |> accepted

let apply command claim =
    Claim.decide today (request (Claim.view claim).Version command) (Some claim)

let decided () =
    opened () |> apply (Command.Decide decision) |> accepted

let paid () =
    decided () |> apply (Command.RecordPayment "2026-08-20") |> accepted

let isInvalid result =
    match result with
    | Error(DomainError.InvalidInput _) -> true
    | _ -> false
