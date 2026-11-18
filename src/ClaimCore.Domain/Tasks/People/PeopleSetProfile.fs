module ClaimCore.Domain.Tasks.People.SetProfile

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    let profile =
        profile @ [ optional (target InputTarget.ProfileDigestEmail) ]
        |> List.map (fun input ->
            if input.Mode = TaskInputMode.Optional then
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
                        target InputTarget.ProfileDigestEmail,
                        profile.DigestEmail
                        |> Option.map (EmailAddress.format >> InputValue.Text)
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
            let profile =
                if Map.containsKey (target InputTarget.ProfileDigestEmail) inputs.Values then
                    command.Profile
                else
                    { command.Profile with
                        DigestEmail = state.Actor.Profile |> Option.bind _.DigestEmail
                    }

            PersonInputs.admitProfile context profile
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

let decideRetained context environment state binding (_: InputMap) =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> PersonRules.existing environment state binding)
    |> Result.map (fun state ->
        [
            PersonRules.change context state state.Actor.Enabled (ProfileValue.Present None) []
        ])
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Authorize =
                fun principal access _ -> PersonRules.permissionFor declaration principal access
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
