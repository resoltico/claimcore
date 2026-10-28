module ClaimCore.Tests.ActSlotProperties

open Expecto
open ClaimCore.TestSupport
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.PropertyHarness

let private values = Gen.int32 (Range.constant -1_000_000 1_000_000)

let private caseActs value =
    Inputs.caseActs Inputs.registration Inputs.decision Inputs.payment "reason"
    |> List.map (fun (act, _) ->
        CaseAct.map (fun _ -> value) (fun _ -> value) (fun _ -> value) (fun _ -> value) act)

let private caseRoundTrip expected kind (inject, project) candidates =
    project (inject expected) = Some expected
    && CaseAct.kind (inject expected) = kind
    && (candidates
        |> List.filter (CaseAct.kind >> ((<>) kind))
        |> List.forall (project >> Option.isNone))

let private caseProperty =
    property {
        let! value = values

        let correction =
            {
                Registration = Amendment.Replace value
                Decision = Change.Replace(value + 1)
                Payment = Change.Clear
            }

        let candidates = caseActs value

        return
            CaseActKind.all
            |> List.forall (fun kind ->
                match (CaseAct.slot kind: CaseActSlot<int, int, int, int>) with
                | CaseActSlot.Bare act -> CaseAct.kind act = kind && List.contains act candidates
                | CaseActSlot.Registration(inject, project)
                | CaseActSlot.Decision(inject, project)
                | CaseActSlot.Payment(inject, project)
                | CaseActSlot.Reason(inject, project) ->
                    caseRoundTrip value kind (inject, project) candidates
                | CaseActSlot.Correction(inject, project) ->
                    caseRoundTrip correction kind (inject, project) candidates)
    }

let private authorityProperty =
    property {
        let! value = values

        let candidates = Inputs.authorityActs [ value; value + 1 ] |> List.map fst

        return
            AuthorityKind.all
            |> List.forall (fun kind ->
                match (AuthorityAct.slot kind: AuthorityActSlot<int>) with
                | AuthorityActSlot.Bare act ->
                    AuthorityAct.kind act = kind && List.contains act candidates
                | AuthorityActSlot.Grant(inject, project) ->
                    project (inject value) = Some value
                    && AuthorityAct.kind (inject value) = kind
                    && (candidates
                        |> List.filter (AuthorityAct.kind >> ((<>) kind))
                        |> List.forall (project >> Option.isNone)))
    }

[<Tests>]
let tests =
    testList
        "typed act slots"
        [
            testCase "case act slots round trip and reject other kinds" (fun () ->
                run "case act slots" caseProperty)
            testCase "authority act slots round trip and reject other kinds" (fun () ->
                run "authority act slots" authorityProperty)
        ]
