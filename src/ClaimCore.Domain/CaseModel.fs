namespace ClaimCore.Domain


open System

/// Exact integer range shared by Domain revisions and interoperable JSON records.
module SafeInteger =
    [<Literal>]
    let Maximum = 9007199254740991L

module Revision =
    [<Literal>]
    let Maximum = SafeInteger.Maximum

    /// JSON's exact-integer bound is a permanent format limit.
    let successor revision =
        let value = Checked.(+) revision 1L

        if value > Maximum then
            raise (OverflowException "The successor is outside the exact-integer format.")

        value

/// A retained snapshot that cannot represent accepted state; never an input diagnostic.
[<RequireQualifiedAccess>]
type RecordViolation =
    | InvalidScalar of InputTarget * Violation
    | InvalidProgress
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
