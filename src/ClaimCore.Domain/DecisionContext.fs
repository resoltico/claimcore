namespace ClaimCore.Domain

open System

/// Recorded operations carry accepted lifetime facts; current previews only enforce the durable frontier.
[<RequireQualifiedAccess>]
type DecisionLifetime =
    | Execution of expiredBefore: DateTimeOffset option
    | Preview of expiredBefore: DateTimeOffset option
    | Recorded

[<NoComparison>]
type DecisionContext =
    {
        // People roots do not store a business date; case transitions require the recorded date.
        BusinessDate: DateOnly option
        ObservedAt: DateTimeOffset
        Principal: PrincipalKey
        Access: AccessView
        // Operational state is supplied by the transaction, without importing the store/transport type.
        Maintenance: bool
        Lifetime: DecisionLifetime
    }

[<RequireQualifiedAccess>]
type ExecutorIssue =
    | ActorNotEnabled
    | HumanRequired
    | OwnerRequired

module DecisionContext =
    let executor context human owner =
        match context.Access.Actor with
        | None -> Error ExecutorIssue.ActorNotEnabled
        | Some actor when not actor.Enabled || actor.Principal <> context.Principal ->
            Error ExecutorIssue.ActorNotEnabled
        | Some _ when human && not (PrincipalKey.isHuman context.Principal) ->
            Error ExecutorIssue.HumanRequired
        | Some actor when
            owner
            && not (
                Access.isEnabledHumanOwner
                    {
                        Actor = actor
                        Grants =
                            context.Access.Installation
                            |> List.map (fun role ->
                                {
                                    Role = role
                                    Scope = Scope.Installation
                                })
                    }
            )
            ->
            Error ExecutorIssue.OwnerRequired
        | _ -> Ok()

    let lifetime context operation =
        match context.Lifetime with
        | DecisionLifetime.Recorded -> Ok()
        | DecisionLifetime.Preview frontier ->
            OperationLifetime.frontierRefusal frontier context.ObservedAt operation
            |> Option.map Error
            |> Option.defaultValue (Ok())
        | DecisionLifetime.Execution frontier ->
            OperationLifetime.refusal frontier context.ObservedAt operation
            |> Option.map Error
            |> Option.defaultValue (Ok())

    let maintenance context =
        match context.Lifetime with
        | DecisionLifetime.Recorded -> Ok()
        | _ when context.Maintenance -> Error Refusal.RegisterMaintenance
        | _ -> Ok()

    /// Identity replay/disclosure is a caller boundary; every new decision uses this same ordering.
    let admit context operation permission stateGate =
        permission
        |> Result.bind (fun () -> lifetime context operation)
        |> Result.bind (fun () -> stateGate ())
        |> Result.bind (fun () -> maintenance context)
