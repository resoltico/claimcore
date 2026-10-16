namespace ClaimCore.ClientProtocol


[<RequireQualifiedAccess>]
type SchemaProblem =
    | ObjectRequired
    | StringRequired
    | BooleanRequired
    | NumberRequired
    | ArrayRequired
    | DuplicateMember
    | UnknownMember
    | MissingMember
    | IntegerRange
    | InvalidValue
    | InvalidFormat of string

/// Opaque, bounded admission cause. No authored values or unknown keys are retained.
type SchemaFailure =
    {
        DeclaredPath: string list
        Problem: SchemaProblem
    }

module internal SchemaFailures =
    let fail path problem =
        Error
            {
                DeclaredPath = path
                Problem = problem
            }

    let require path problem accepted =
        if accepted then Ok() else fail path problem
