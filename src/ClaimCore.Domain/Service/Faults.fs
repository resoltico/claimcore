namespace ClaimCore.Domain



[<RequireQualifiedAccess>]
type CoreFault =
    | InternalError
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | OperationContentConflict

module CoreFaults =
    let internal action =
        function
        | CoreFault.StoreUnavailable -> RecommendedAction.RetrySafe
        | CoreFault.InternalError
        | CoreFault.StoreIntegrityError
        | CoreFault.SchemaMismatch
        | CoreFault.OperationContentConflict -> RecommendedAction.StopAndInvestigate

    let vocabulary =
        Vocabulary.ofUnion<CoreFault> "fault" TokenCasing.ScreamingSnake (Some "CORE_")

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary

type CoreFault with
    member this.Action = CoreFaults.action this
