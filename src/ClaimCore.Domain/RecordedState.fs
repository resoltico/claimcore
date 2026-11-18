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

[<RequireQualifiedAccess>]
type Authority =
    | Actor of ActorId
    | Operator

module Authority =
    let actorId =
        function
        | Authority.Actor actor -> Some actor
        | Authority.Operator -> None

    let reference find =
        function
        | Authority.Actor actor -> find actor |> Option.map AuthorityRef.Actor
        | Authority.Operator -> Some AuthorityRef.Operator

    let ofActorId =
        function
        | Some actor -> Authority.Actor actor
        | None -> Authority.Operator

/// Accepted position and attribution belong to an effect, independently of its private content.
type SubjectStamp =
    {
        Position: EffectPosition
        OperationId: OperationId
        Authority: Authority
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
