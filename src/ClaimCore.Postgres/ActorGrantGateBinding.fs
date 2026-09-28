namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ActorGrantGateQueries

module internal ActorGrantGateBinding =
    let context commitments binding caseId action =
        binding
        |> Option.map (fun actor ->
            {
                Binding = actor
                CaseId = caseId
                Action = action
                Suppression = commitments
            })

    let validCommandAction action request =
        match request.Command, action with
        | Command.Open _, (EndpointAction.PrepareNewCase | EndpointAction.ExecuteNewCase) -> true
        | Command.Open _, _ -> false
        | _, (EndpointAction.PrepareCommand | EndpointAction.ExecuteCommand) -> true
        | _ -> false

    let bindOpen
        commitments
        connection
        transaction
        revision
        principal
        action
        (request: CommandRequest)
        cancellationToken
        =
        task {
            let! actor =
                authorize
                    connection
                    transaction
                    revision
                    principal
                    action
                    (Some ResourceScope.Installation)
                    cancellationToken

            match actor with
            | None -> return None
            | Some _ ->
                let! blocked =
                    blockedReference connection transaction request.CaseReference cancellationToken

                if blocked then
                    return None
                else
                    let! accepted =
                        acceptedCaseId connection transaction request.OperationId cancellationToken

                    let! retained =
                        retainedCaseId connection transaction request.OperationId cancellationToken

                    let! existing =
                        caseIdByReference
                            connection
                            transaction
                            request.CaseReference
                            cancellationToken

                    let caseId =
                        accepted
                        |> Option.orElse retained
                        |> Option.orElse existing
                        |> Option.defaultWith Guid.NewGuid

                    let! available = availableCase connection transaction caseId cancellationToken

                    return
                        if available then
                            context commitments actor (Some caseId) action
                        else
                            None
        }

    let bindExisting
        commitments
        connection
        transaction
        revision
        principal
        action
        (request: CommandRequest)
        cancellationToken
        =
        task {
            let! caseId =
                caseIdByReference connection transaction request.CaseReference cancellationToken

            let resource = caseId |> Option.map ResourceScope.Case

            let! actor =
                authorize
                    connection
                    transaction
                    revision
                    principal
                    action
                    resource
                    cancellationToken

            return context commitments actor caseId action
        }
