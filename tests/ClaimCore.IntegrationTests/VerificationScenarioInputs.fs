module ClaimCore.IntegrationTests.VerificationScenarioInputs

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport


let private authored (inputs: InputMap) =
    let atom =
        function
        | InputValue.Text value -> SubmissionValue.Text value
        | InputValue.Integer value -> SubmissionValue.Integer value
        | InputValue.Boolean value -> SubmissionValue.Boolean value
        | InputValue.Null -> SubmissionValue.Clear

    let values = inputs.Values |> Map.map (fun _ -> atom)

    inputs.Collections
    |> Map.fold
        (fun values collection rows ->
            let key =
                match collection with
                | TaskCollectionId.PeopleGrants -> Submissions.input InputTarget.Grants

            Map.add
                key
                (SubmissionValue.Rows(
                    rows |> List.map (fun row -> row.Values |> Map.map (fun _ -> atom))
                ))
                values)
        values

let internal livePayload (reference: CaseReference) (value: RetainedOperation) =
    let root = value.Root
    let change = value.Changes |> List.find (fun change -> change.Kind = PartKind.Case)

    let reference =
        match change.Value with
        | PartValue.Case current -> current.Reference |> Option.defaultValue reference
        | _ -> failtest "Case part required"

    let snapshot = PartChange.caseSnapshot reference change |> Option.get

    let submission =
        Submissions.create
            (TaskId.token root.Structure.Task)
            (OperationId.value root.Header.OperationId)
            root.Structure.Binding
            (value.Inputs |> Option.get |> authored)

    submission, snapshot

let internal updateCase
    (request: OperationSubmission)
    (snapshot: CaseSnapshot)
    (value: RetainedOperation)
    =
    let command = Submissions.command request

    let content =
        CaseState.fields snapshot.State
        |> Option.map (fun fields ->
            {
                CaseContent.Registration = fields.Registration
                Finance = fields.Finance
            })

    let changes =
        value.Changes
        |> List.map (fun change ->
            match change.Value with
            | PartValue.Case before ->
                { change with
                    Revision = snapshot.Revision
                    NaturalKey =
                        change.NaturalKey
                        |> Option.map (fun _ -> CaseReference.format snapshot.Reference)
                    Value =
                        PartValue.Case
                            { before with
                                Reference =
                                    before.Reference |> Option.map (fun _ -> snapshot.Reference)
                                Lifecycle = CaseState.lifecycle snapshot.State
                                Status =
                                    CaseState.fields snapshot.State
                                    |> Option.map _.Status
                                    |> Option.defaultValue before.Status
                                Content = content
                            }
                }
            | _ -> change)

    let changes =
        changes
        |> List.map (fun change ->
            { change with
                Stamp =
                    { change.Stamp with
                        OperationId = CaseFixtures.operationId request.OperationId
                    }
            })

    let nonce =
        value.Root.Structure.Compartments
        |> List.tryPick Compartment.content
        |> Option.map fst
        |> Option.defaultValue Array.empty

    recreate
        { value.Root.Header with
            OperationId = CaseFixtures.operationId request.OperationId
        }
        request.Task
        command.Binding
        command.Inputs
        changes
        nonce

let internal updatePeople taskName values mutate (value: RetainedOperation) =
    let request =
        Submissions.create
            taskName
            (OperationId.value value.Root.Header.OperationId)
            value.Root.Structure.Binding
            values
        |> Submissions.command

    let changes =
        value.Changes
        |> List.map (fun change ->
            match change.Value with
            | PartValue.Person person ->
                { change with
                    Value = PartValue.Person(mutate person)
                }
            | _ -> failtest "Person part required")

    let nonce =
        value.Root.Structure.Compartments
        |> List.tryPick Compartment.content
        |> Option.map fst
        |> Option.defaultValue Array.empty

    recreate
        value.Root.Header
        (TaskId.ofLiteral taskName)
        request.Binding
        request.Inputs
        changes
        nonce

let internal executor id (value: RetainedOperation) =
    rehashed
        { value.Root.Header with ActorId = id }
        value.Root.Structure.Task
        value.Root.Structure.Binding
        value.Root.Structure.Retained
        value.Root.Structure.Effects
        value.Root.Structure.Compartments

let internal target subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let internal register fixture (actor: ServiceContext) n principal =
    task {
        let! outcome =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture n))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.Inputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((token))
            }))

        acceptedPeople outcome
    }
