module ClaimCore.IntegrationTests.VerificationScenarioInputs

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport


let internal livePayload (value: RetainedOperation) =
    match value.Values with
    | RetainedOperationValues.Case(Some values) ->
        (Case.canonical values.Command).Authored, values.Decision.After
    | _ -> failtest "Available accepted case values are required"

let internal updateCase
    (request: UnvalidatedCaseRequest)
    (snapshot: CaseSnapshot)
    (value: RetainedOperation)
    =
    let command =
        Case.parse request
        |> Result.defaultWith (fun error -> failtestf "Case mutation: %A" error)

    let previous =
        match value.Values with
        | RetainedOperationValues.Case(Some previous) -> previous
        | _ -> failtest "Case values required"

    let selected =
        match command.Act with
        | CaseAct.RemovePayment id -> Some id
        | _ -> None

    let scope =
        CommandSkeleton.Case
            {
                CaseId = snapshot.CaseId
                Reference = snapshot.Reference
                ExpectedRevision = command.ExpectedRevision
                Act = CaseAct.kind command.Act
                SelectedPayment = selected
            }

    let nonce =
        value.Root.Structure.Compartments
        |> List.tryPick Compartment.content
        |> Option.map fst
        |> Option.defaultValue Array.empty

    recreate
        { value.Root.Header with
            OperationId = command.OperationId
        }
        scope
        (OperationValues.Case
            {
                Command = command
                Decision =
                    { previous.Decision with
                        After = snapshot
                    }
            })
        nonce

let internal updatePeople act (value: RetainedOperation) =
    let scope =
        match value.Root.Structure.Command with
        | CommandSkeleton.People scope -> scope
        | _ -> failtest "People root required"

    let profile =
        match value.Values with
        | RetainedOperationValues.People(RetainedProfile.Available profile) -> Some profile
        | _ -> None

    let nonce =
        value.Root.Structure.Compartments
        |> List.tryPick Compartment.content
        |> Option.map fst
        |> Option.defaultValue Array.empty

    recreate
        value.Root.Header
        (CommandSkeleton.People { scope with Act = act })
        (OperationValues.People { Profile = profile })
        nonce

let internal executor id (value: RetainedOperation) =
    OperationRoot.append
        { value.Root.Header with ActorId = id }
        value.Root.Structure.Command
        value.Root.Structure.Effects
        value.Root.Structure.Compartments
    |> Result.defaultWith (fun error -> failtestf "Executor mutation: %A" error)

let internal target subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let internal register fixture (actor: ServiceContext) n principal =
    task {
        let! outcome =
            (Service.execute
                actor
                (OperationInput.People(
                    peopleRequest
                        (id fixture n)
                        principal
                        (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (token))

        acceptedPeople outcome
    }
