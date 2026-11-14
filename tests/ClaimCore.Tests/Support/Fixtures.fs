module ClaimCore.Tests.Fixtures

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain

let today = DateOnly(2026, 9, 7)

/// Complete unrelated availability members so focused raw vectors isolate their intended defect.
let completeAdviceWire (raw: string) =
    let value = JsonNode.Parse raw |> nonNull

    let offered =
        (value["acts"] |> nonNull).AsArray()
        |> Seq.map (fun item -> (item |> nonNull).GetValue<string>())
        |> Set.ofSeq

    let maintenance = value["maintenance"] |> nonNull
    let paused = (maintenance["active"] |> nonNull).GetValue<bool>()
    let blocked = JsonArray()

    for token in (CaseActKind.all |> List.map CaseActKind.token) do
        if not (Set.contains token offered) then
            let item = JsonObject()
            item["act"] <- JsonValue.Create token
            let reason = JsonObject()

            reason["code"] <-
                JsonValue.Create(
                    if paused then
                        "REGISTER_MAINTENANCE"
                    else
                        "RESOURCE_UNAVAILABLE"
                )

            reason["action"] <-
                JsonValue.Create(if paused then "READ_CURRENT" else "ASK_FOR_ACCESS")

            item["reason"] <- reason
            blocked.Add item

    value["blocked"] <- blocked
    value.ToJsonString()

let registration = Inputs.exampleRegistration

let decision = Inputs.decision

let request revision act =
    {
        OperationId = Guid.Parse("20000000-0000-7000-8000-000000000001")
        CaseReference = "UNIT-001"
        ExpectedRevision = revision
        Act = act
    }

let accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected acceptance."

let private acceptedRecord (decision: CaseDecision) =
    Case.ofAccepted decision.After |> accepted

let opened () =
    CaseFixtures.decide today Map.empty (request 0L (AuthoredCaseAct.Open registration)) None
    |> accepted
    |> acceptedRecord

let applyWith context act claim =
    CaseFixtures.decide
        today
        context
        (request (Revision.value (Case.snapshot claim).Revision) act)
        (Some claim)
    |> Result.map acceptedRecord

let apply act claim = applyWith Map.empty act claim

let decided () =
    opened () |> apply (AuthoredCaseAct.Decide decision) |> accepted

let paid () =
    decided () |> apply (AuthoredCaseAct.RecordPayment Inputs.payment) |> accepted

let isInvalid result =
    match result with
    | Error(CaseDecisionError.Refused(Refusal.InvalidInput _)) -> true
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
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-7000-8000-000000000001")
        Reference = fields.CaseReference
        Revision = CaseFixtures.revision revision
        State = CaseState.Active(fields)
    }
