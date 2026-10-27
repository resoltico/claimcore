namespace ClaimCore.Application


open System
open ClaimCore.Domain

/// A position independently retained by an operator or returned in an accepted receipt.
type ExpectedPosition =
    {
        Sequence: int64
        EntryHash: byte array
    }

module ExpectedPositions =
    let isValid (position: ExpectedPosition) =
        position.Sequence >= 0L
        && position.Sequence <= SafeInteger.Maximum
        && not (obj.ReferenceEquals(position.EntryHash, null))
        && position.EntryHash.Length = 32

[<RequireQualifiedAccess>]
type VerificationFinding =
    | Link
    | EntryHash
    | Commitment
    | PayloadFormat
    | RevisionSequence
    | HistoryStart
    | StateSequence
    | ErasureIncomplete
    | PayloadWithoutErasure
    | AuthorityFold
    | ActorNotEnabled
    | HumanRequired
    | OwnerRequired
    | CaseProjection
    | AuthorityProjection
    | Head
    | ExpectedPosition

type VerificationDivergence =
    {
        Sequence: int64 option
        Finding: VerificationFinding
    }

[<RequireQualifiedAccess>]
type VerificationResult =
    | Verified of LedgerPosition
    | Diverged of VerificationDivergence

module VerificationFindings =
    let vocabulary =
        Vocabulary.ofUnion<VerificationFinding> "finding" TokenCasing.ScreamingSnake None

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary

module VerificationChecks =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()

    let divergence sequence finding =
        {
            Sequence = sequence
            Finding = finding
        }

    let require sequence finding condition =
        if condition then
            Ok()
        else
            Error(divergence sequence finding)

    let capture sequence finding read =
        try
            Ok(read ())
        with
        | :? InvalidOperationException
        | :? InvalidCastException
        | :? ArgumentException
        | :? FormatException
        | :? OverflowException -> Error(divergence sequence finding)

    let known sequence finding =
        function
        | Some value -> Ok value
        | None -> Error(divergence sequence finding)
