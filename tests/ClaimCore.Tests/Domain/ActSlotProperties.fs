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
            candidates
            |> List.forall (fun act ->
                let mapped = AuthorityAct.map ((+) 1) act
                let restored = AuthorityAct.map (fun value -> value - 1) mapped
                let rejected = AuthorityAct.traverse (fun _ -> Error()) act

                let hasGrant =
                    match act with
                    | AuthorityAct.Grant _
                    | AuthorityAct.Revoke _ -> true
                    | _ -> false

                restored = act
                && AuthorityAct.kind mapped = AuthorityAct.kind act
                && Result.isError rejected = hasGrant)

    }

let private descriptions () =
    for row in Inventory.rows "caseActs" do
        let token = row.GetProperty("token").GetString() |> nonNull
        let kind = CaseActKind.parse token |> Option.get
        let description = CaseActKind.describe kind

        let family =
            match description.Family with
            | ActFamily.Business -> "BUSINESS"
            | ActFamily.Stewardship -> "STEWARDSHIP"

        let payload =
            match description.Payload with
            | ActPayload.Bare _ -> "BARE"
            | ActPayload.Registration -> "REGISTRATION"
            | ActPayload.Decision -> "DECISION"
            | ActPayload.Payment -> "PAYMENT"
            | ActPayload.Reason -> "REASON"
            | ActPayload.Correction -> "CORRECTION"

        Expect.equal
            family
            (row.GetProperty("family").GetString() |> nonNull)
            "Independent act family"

        Expect.equal
            payload
            (row.GetProperty("payload").GetString() |> nonNull)
            "Independent act payload"

        Expect.equal
            description.Revision
            (if token = "OPEN" then
                 RevisionInput.New
             else
                 RevisionInput.Expected)
            "Only opening creates a new case"

[<Tests>]
let tests =
    testList
        "typed act structure"
        [
            testCase "descriptions agree with the independent act inventory" descriptions
            testCase "case act slots round trip and reject other kinds" (fun () ->
                run "case act slots" caseProperty)
            testCase
                "authority mapping and traversal preserve kinds and reject grant failures"
                (fun () -> run "authority act traversal" authorityProperty)
        ]
