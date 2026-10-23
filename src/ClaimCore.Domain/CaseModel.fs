namespace ClaimCore.Domain

open ClaimCore.Contract

open System

/// Exact integer range shared by Domain revisions and interoperable JSON records.
module SafeInteger =
    [<Literal>]
    let Maximum = JsonValue.MaximumInteger

module Revision =
    [<Literal>]
    let Maximum = SafeInteger.Maximum

/// A retained snapshot that cannot represent accepted state; never an input diagnostic.
[<RequireQualifiedAccess>]
type RecordViolation =
    | InvalidScalar of InputTarget * Violation
    | IncompleteDecision
    | InvalidProgress
    | NonCanonicalFields
    | RevisionOutOfRange
    | ReferenceMismatch

/// Small Result computation expression; ordinary business refusals are values, not exceptions.
module internal ResultFlow =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()
