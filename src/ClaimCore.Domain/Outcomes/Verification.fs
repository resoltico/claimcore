namespace ClaimCore.Domain


open System

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
        Sequence: Sequence option
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
