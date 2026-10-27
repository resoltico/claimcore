module ClaimCore.Tests.Fixtures

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain

let today = DateOnly(2026, 9, 7)

let registration =
    { Inputs.registration with
        ClaimantName = "Example Claimant Ltd"
        InsurerName = "Example Alleged Insurer"
    }

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
    CaseFixtures.decide today (request 0L (UnvalidatedCaseAct.Open registration)) None
    |> accepted

let apply act claim =
    CaseFixtures.decide today (request (Case.snapshot claim).Revision act) (Some claim)

let decided () =
    opened () |> apply (UnvalidatedCaseAct.Decide decision) |> accepted

let paid () =
    decided ()
    |> apply (UnvalidatedCaseAct.RecordPayment Inputs.payment)
    |> accepted

let isInvalid result =
    match result with
    | Error(Refusal.InvalidInput _) -> true
    | _ -> false

let actRequest revision act =
    { request revision UnvalidatedCaseAct.Close with
        Act = act
    }

let viewFields (snapshot: CaseSnapshot) =
    match snapshot.State with
    | (CaseState.Active(fields) | CaseState.Voided(fields)) -> fields
    | CaseState.Erased -> failtest "Expected retained business fields"

let snapshot revision (fields: CaseFields) =
    {
        Reference = fields.CaseReference
        Revision = revision
        State = CaseState.Active(fields)
    }
