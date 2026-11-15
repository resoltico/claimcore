module ClaimCore.Domain.Tasks.People.SetProfile

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    let profile =
        profile
        |> List.map (fun input ->
            if input.Input = target InputTarget.ProfileContact then
                { input with Clearable = true }
            else
                input)

    create
        "people.setProfile"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageAccess ]
        (operation :: (required TaskInputId.ActorId |> bound) :: revision :: profile)
        []

type private Command = { Profile: ActorProfile }

let private parse inputs =
    PersonInputs.profile inputs |> Result.map (fun profile -> { Profile = profile })

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    let offered = PersonRules.availability declaration environment state binding

    match state |> Option.bind (fun state -> state.Actor.Profile) with
    | None -> offered
    | Some profile ->
        { offered with
            Seeds =
                Map.ofList
                    [
                        target InputTarget.ProfileDisplayName,
                        InputValue.Text(PartyName.format profile.DisplayName)
                        target InputTarget.ProfileContact,
                        profile.Contact
                        |> Option.map (ContactText.format >> InputValue.Text)
                        |> Option.defaultValue InputValue.Null
                    ]
        }

let decide context environment (state: Person option) binding inputs =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> PersonRules.existing environment state binding)
    |> Result.bind (fun state ->
        parse inputs
        |> Result.bind (fun command ->
            PersonInputs.admitProfile context command.Profile
            |> Result.map (fun profile ->
                [
                    PersonRules.change
                        context
                        state
                        state.Actor.Enabled
                        (ProfileValue.Present(Some profile))
                        []
                ])))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
