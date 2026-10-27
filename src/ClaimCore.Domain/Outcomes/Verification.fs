namespace ClaimCore.Domain


open System

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module VerificationFinding =
    let vocabulary =
        Vocabulary.ofUnion<VerificationFinding> "finding" TokenCasing.ScreamingSnake None

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary
