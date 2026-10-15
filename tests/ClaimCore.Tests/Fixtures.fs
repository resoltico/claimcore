module ClaimCore.Tests.Fixtures

open System
open Expecto
open ClaimCore.Domain

let today = DateOnly(2026, 9, 7)

let registration =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
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

let request revision command =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000001")
        CaseReference = "UNIT-001"
        ExpectedRevision = revision
        Act = CaseAct.Business command
    }

let accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected acceptance."

let opened () =
    Case.decide today (request 0L (BusinessCommand.Open registration)) None
    |> accepted

let apply command claim =
    Case.decide today (request (Case.snapshot claim).Revision command) (Some claim)

let decided () =
    opened () |> apply (BusinessCommand.Decide decision) |> accepted

let paid () =
    decided () |> apply (BusinessCommand.RecordPayment "2026-08-20") |> accepted

let isInvalid result =
    match result with
    | Error(CaseRefusal.InvalidInput _) -> true
    | _ -> false

let actRequest revision act =
    { request revision BusinessCommand.Close with
        Act = act
    }

let viewFields (snapshot: CaseSnapshot) =
    match snapshot.View with
    | CaseView.Live(fields, _) -> fields
    | CaseView.Erased -> failtest "Expected retained business fields"

let snapshot revision (fields: CaseFields) =
    {
        Reference = fields.CaseReference
        Revision = revision
        View = CaseView.Live(fields, Disposition.Active)
    }
