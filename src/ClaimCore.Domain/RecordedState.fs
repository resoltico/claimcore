namespace ClaimCore.Domain

open System

/// A retained snapshot that cannot represent accepted state; never an input diagnostic.
[<RequireQualifiedAccess>]
type RecordViolation =
    | InvalidScalar of InputTarget * Violation
    | InvalidFinancialPosition
    | InvalidPaymentState
    | InvalidSubjectState
    | InvalidRevisionSequence
    | InvalidHistoryStart
    | RevisionOutOfRange
    | ReferenceMismatch

/// Accepted position and attribution belong to an effect, independently of its private content.
type SubjectStamp =
    {
        Position: EffectPosition
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
    }

/// Small Result computation expression; ordinary business refusals are values, not exceptions.
module internal ResultFlow =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()
