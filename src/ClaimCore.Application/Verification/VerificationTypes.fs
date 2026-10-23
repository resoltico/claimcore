namespace ClaimCore.Application

open ClaimCore.Contract

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
    | DispositionSequence
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
    let all =
        [
            VerificationFinding.Link, "LINK"
            VerificationFinding.EntryHash, "ENTRY_HASH"
            VerificationFinding.Commitment, "COMMITMENT"
            VerificationFinding.PayloadFormat, "PAYLOAD_FORMAT"
            VerificationFinding.RevisionSequence, "REVISION_SEQUENCE"
            VerificationFinding.HistoryStart, "HISTORY_START"
            VerificationFinding.DispositionSequence, "DISPOSITION_SEQUENCE"
            VerificationFinding.ErasureIncomplete, "ERASURE_INCOMPLETE"
            VerificationFinding.PayloadWithoutErasure, "PAYLOAD_WITHOUT_ERASURE"
            VerificationFinding.AuthorityFold, "AUTHORITY_FOLD"
            VerificationFinding.ActorNotEnabled, "ACTOR_NOT_ENABLED"
            VerificationFinding.HumanRequired, "HUMAN_REQUIRED"
            VerificationFinding.OwnerRequired, "OWNER_REQUIRED"
            VerificationFinding.CaseProjection, "CASE_PROJECTION"
            VerificationFinding.AuthorityProjection, "AUTHORITY_PROJECTION"
            VerificationFinding.Head, "HEAD"
            VerificationFinding.ExpectedPosition, "EXPECTED_POSITION"
        ]

    let token finding =
        all |> List.find (fst >> (=) finding) |> snd

module internal VerificationChecks =
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
