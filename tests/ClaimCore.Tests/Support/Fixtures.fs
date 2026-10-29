module ClaimCore.Tests.Fixtures

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain

let today = DateOnly(2026, 9, 7)

let registration = Inputs.exampleRegistration

let decision = Inputs.decision

let request revision act =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000001")
        CaseReference = "UNIT-001"
        ExpectedRevision = revision
        Act = act
    }

let accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected acceptance."

let opened () =
    CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open registration)) None
    |> accepted

let apply act claim =
    CaseFixtures.decide
        today
        (request (Revision.value (Case.snapshot claim).Revision) act)
        (Some claim)

let decided () =
    opened () |> apply (AuthoredCaseAct.Decide decision) |> accepted

let paid () =
    decided () |> apply (AuthoredCaseAct.RecordPayment Inputs.payment) |> accepted

let isInvalid result =
    match result with
    | Error(Refusal.InvalidInput _) -> true
    | _ -> false

let actRequest revision act =
    { request revision (AuthoredCaseAct.Bare BareAct.Close) with
        Act = act
    }

let viewFields (snapshot: CaseSnapshot) =
    match snapshot.State with
    | (CaseState.Active(fields) | CaseState.Voided(fields)) -> fields
    | CaseState.Erased -> failtest "Expected retained business fields"

let snapshot revision (fields: CaseFields) =
    {
        Reference = fields.CaseReference
        Revision = CaseFixtures.revision revision
        State = CaseState.Active(fields)
    }
