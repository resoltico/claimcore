module ClaimCore.Tests.Tasks.PeopleIdentityTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Change = ClaimCore.Domain.Tasks.People.ChangeSignInIdentity
module Recover = ClaimCore.Domain.Tasks.People.RecoverOwner
module Register = ClaimCore.Domain.Tasks.People.Register

let private identity subject =
    PrincipalKey.human "https://issuer.example/" subject |> accepted

let private signInInputs principal =
    let _, issuer, subject = PrincipalKey.parts principal

    [
        text InputTarget.PrincipalIssuer issuer
        text InputTarget.PrincipalSubject subject
    ]

let private change number owner (state: Person) principal =
    let context = context number

    let environment =
        { peopleEnvironment state.Actor.Principal with
            PrincipalOwner = owner
        }

    match Change.task with
    | ClaimCore.Domain.Tasks.Task.Person task ->
        task.Decide
            context
            environment
            (Some state)
            (personBinding state)
            (inputs (signInInputs principal))
    | _ -> failtest "Expected an account task."

let private recover number environment state binding values =
    let context = context number

    let context =
        { context with
            Stamp =
                { context.Stamp with
                    Authority = Authority.Operator
                }
        }

    match Recover.task with
    | ClaimCore.Domain.Tasks.Task.Person task ->
        task.Decide context environment state binding values
    | _ -> failtest "Expected an operator account task."

[<Tests>]
let tests =
    testList
        "mutable sign-in identity and owner recovery"
        [
            testCase
                "a replacement identity reserves the new key and leaves the actor and profile intact"
                (fun () ->
                    let old = identity "old-sub"
                    let replacement = identity "new-sub"
                    let initial = personWith old
                    let changes = change 3 None initial replacement |> accepted

                    Expect.equal
                        changes.Head.NaturalKey
                        (Some(PrincipalKey.naturalKey replacement))
                        "First use introduces the key"

                    let updated = personApply initial changes

                    Expect.equal
                        updated.Actor
                        { initial.Actor with
                            Principal = replacement
                        }
                        "Only identity changes"

                    Expect.equal updated.Grants initial.Grants "No assignment changes"

                    Expect.equal
                        (Revision.value updated.Revision)
                        2L
                        "The existing account advances one revision"

                    change 4 None updated replacement
                    |> expectRefusal (Refusal.People PeopleRefusal.NoChanges)

                    let returnToOld = change 4 (Some initial.Id) updated old |> accepted

                    Expect.isNone
                        returnToOld.Head.NaturalKey
                        "Returning consumes no second natural-key slot"

                    Expect.equal
                        (personApply updated returnToOld).Actor
                        initial.Actor
                        "Same account with the earlier key")

            testCase "registration and reassignment cannot take a reserved identity" (fun () ->
                let original = person ()
                let another = actorId
                let reserved = identity "reserved-sub"

                change 3 (Some another) original reserved
                |> expectRefusal (Refusal.People PeopleRefusal.IdentityReserved)

                Register.decide
                    (context 3)
                    { peopleEnvironment reserved with
                        PrincipalOwner = Some another
                    }
                    None
                    (Binding.PeopleNew Revision.zero)
                    (peopleInputs reserved)
                |> expectRefusal (Refusal.People PeopleRefusal.ActorAlreadyRegistered))

            testCase
                "operator recovery requires an existing installation and a new owner's name"
                (fun () ->
                    let principal = identity "recovery-sub"
                    let inputsWithoutName = inputs (signInInputs principal)
                    let environment = peopleEnvironment principal

                    recover
                        3
                        { environment with ActorCount = 0 }
                        None
                        (Binding.PeopleNew Revision.zero)
                        inputsWithoutName
                    |> expectRefusal (Refusal.People PeopleRefusal.ActorUnknown)

                    let issues =
                        recover
                            3
                            environment
                            None
                            (Binding.PeopleNew Revision.zero)
                            inputsWithoutName
                        |> expectInputs

                    Expect.equal
                        (issues |> List.map _.Input)
                        [ TaskInputId.Target InputTarget.ProfileDisplayName ]
                        "Only the name is missing"

                    let change =
                        recover
                            3
                            { environment with Maintenance = true }
                            None
                            (Binding.PeopleNew Revision.zero)
                            (inputs (signInInputs principal @ profileInputs))
                        |> accepted
                        |> List.exactlyOne

                    let recovered = Person.apply None change |> accepted

                    Expect.isTrue
                        (Person.access recovered |> Access.isEnabledHumanOwner)
                        "A fresh owner can sign in"

                    Expect.equal
                        change.Stamp.Authority
                        Authority.Operator
                        "The operator records recovery")

            testCase
                "owner recovery can enable an existing owner and repeated recovery changes nothing"
                (fun () ->
                    let initial = owner ()
                    let principal = initial.Actor.Principal

                    let environment =
                        { peopleEnvironment principal with
                            Maintenance = true
                            PrincipalOwner = Some initial.Id
                        }

                    let disabled =
                        match ClaimCore.Domain.Tasks.People.Disable.task with
                        | ClaimCore.Domain.Tasks.Task.Person task ->
                            task.Decide
                                (context 3)
                                { environment with Maintenance = false }
                                (Some initial)
                                (personBinding initial)
                                (inputs [])
                            |> accepted
                            |> personApply initial
                        | _ -> failtest "Expected account disable"

                    let recovered =
                        recover
                            4
                            environment
                            (Some disabled)
                            (personBinding disabled)
                            (inputs (signInInputs principal))
                        |> accepted
                        |> personApply disabled

                    Expect.isTrue
                        recovered.Actor.Enabled
                        "Recovery enables the account while maintenance is active"

                    Expect.equal
                        recovered.Grants
                        disabled.Grants
                        "Recovery preserves existing grant provenance"

                    recover
                        5
                        environment
                        (Some recovered)
                        (personBinding recovered)
                        (inputs (signInInputs principal))
                    |> expectRefusal (Refusal.People PeopleRefusal.NoChanges))
        ]
