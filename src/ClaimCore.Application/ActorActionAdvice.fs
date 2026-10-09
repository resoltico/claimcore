namespace ClaimCore.Application

module internal ActorActionAdvice =
    /// Advisory resource projection only. Durable execution never consults this value.
    let mayEditCommands principal authority resource =
        match resource with
        | ResourceScope.Installation ->
            ActorAuthorization.can principal authority EndpointAction.ExecuteNewCase resource
        | ResourceScope.Case _ ->
            ActorAuthorization.can principal authority EndpointAction.ExecuteCommand resource
        | ResourceScope.Operation _ -> false

    let allowedRecoveryActions principal authority resource =
        match resource with
        | ResourceScope.Operation _ ->
            [
                EndpointAction.RecoveryResolve
                EndpointAction.RecoveryDismiss
                EndpointAction.RecoveryExport
            ]
            |> List.filter (fun action ->
                ActorAuthorization.can principal authority action resource)
        | ResourceScope.Installation
        | ResourceScope.Case _ -> []
