module ClaimCore.Tests.Tasks.PeopleGrantTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Grant = ClaimCore.Domain.Tasks.People.Grant

[<Tests>]
let tests =
    testList
        "task people.grant"
        [
            testCase "available role choices follow the target's principal kind" (fun () ->
                let service = PrincipalKey.service "https://issuer.example/" "service" |> accepted
                let state = personWith service

                let offer =
                    Grant.availability
                        (peopleEnvironment service)
                        (Some state)
                        (personBinding state)

                Expect.equal
                    offer.Choices[TaskInputId.Target InputTarget.Role]
                    [
                        "CASE_READER"
                        "CASE_REGISTRAR"
                        "CASE_HANDLER"
                        "PAYMENT_RECORDER"
                        "AUDITOR"
                    ]
                    "Service accounts cannot be offered human-only roles"

                Expect.equal
                    offer.Seeds[TaskInputId.Scope]
                    (InputValue.Text "INSTALLATION")
                    "The task owns the initial scope")
            testCase
                "accepted assignments carry only the changed grant and regrant refreshes provenance"
                (fun () ->
                    let state = person ()
                    let environment = peopleEnvironment newPrincipal

                    let changes =
                        Grant.decide
                            (context 3)
                            environment
                            (Some state)
                            (personBinding state)
                            (grantInputs Role.CaseReader "INSTALLATION")
                        |> accepted

                    let after = personApply state changes

                    let key =
                        {
                            ActorGrant.Role = Role.CaseReader
                            Scope = Scope.Installation
                        }

                    Expect.equal
                        (Map.find key after.Grants)
                        (context 3).Stamp.Position
                        "Accepted position"

                    let changes =
                        Grant.decide
                            (context 4)
                            environment
                            (Some after)
                            (personBinding after)
                            (grantInputs Role.CaseReader "INSTALLATION")
                        |> accepted

                    let after = personApply after changes
                    Expect.equal after.Grants.Count 1 "No duplicate grant"

                    Expect.equal
                        (Map.find key after.Grants)
                        (context 4).Stamp.Position
                        "Regrant establishes the current accepting position"

                    match changes.Head.Value with
                    | PartValue.Person value ->
                        Expect.equal value.Grants.Length 1 "Sparse accepted assignment"
                    | _ -> failtest "Expected a person value")
            testCase
                "target resolution, role scopes, human requirements and reviewed revision are enforced"
                (fun () ->
                    let state = person ()
                    let id = CaseId.ofOperationId (operation 9)

                    let environment =
                        { peopleEnvironment newPrincipal with
                            TargetCases = Map.ofList [ GrantTarget.CaseId id, id ]
                        }

                    let caseGrant =
                        inputs
                            [
                                text InputTarget.Role "OWNER"
                                TaskInputId.Scope, InputValue.Text "CASE"
                                TaskInputId.CaseId, InputValue.Text(CaseId.format id)
                            ]

                    Grant.decide
                        (context 3)
                        environment
                        (Some state)
                        (personBinding state)
                        caseGrant
                    |> expectRefusal (Refusal.People PeopleRefusal.RoleScopeNotApplicable)

                    Grant.decide
                        (context 3)
                        { environment with
                            TargetCases = Map.empty
                        }
                        (Some state)
                        (personBinding state)
                        caseGrant
                    |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable)

                    Grant.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.People(state.Id, Revision.zero))
                        (grantInputs Role.CaseReader "INSTALLATION")
                    |> expectRefusal (Refusal.People PeopleRefusal.ActorChanged)

                    let service =
                        PrincipalKey.service "https://issuer.example/" "service" |> accepted

                    let state = personWith service

                    Grant.decide
                        (context 3)
                        (peopleEnvironment service)
                        (Some state)
                        (personBinding state)
                        (grantInputs Role.DataSteward "INSTALLATION")
                    |> expectRefusal (Refusal.People PeopleRefusal.HumanPrincipalRequired))
        ]
