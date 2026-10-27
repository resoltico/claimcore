namespace ClaimCore.Contract

open ClaimCore.Domain

open System

type RuntimeContext =
    {
        ProductVersion: string
        EffectiveBusinessDate: DateOnly
        TimeZoneId: string
    }

[<RequireQualifiedAccess>]
type View =
    | Cases
    | Authority
    | Operations

[<RequireQualifiedAccess>]
type InstallationOffer =
    | Open
    | AuthorityAct of AuthorityKind

type CorrectionChoices =
    {
        Registration: CorrectionGroupAction list
        Decision: CorrectionGroupAction list
        Payment: CorrectionGroupAction list
    }

[<RequireQualifiedAccess>]
type CaseActOffer =
    | Act of act: CaseActKind * expectedRevision: int64
    | Correct of expectedRevision: int64 * choices: CorrectionChoices

type CaseAdvice =
    {
        Acts: CaseActOffer list
        History: HistoryDetail list
    }

type ActorContext =
    {
        Runtime: RuntimeContext
        Capabilities: Capability list
        Views: View list
        Offers: InstallationOffer list
    }

type CaseReadModel =
    {
        Snapshot: CaseSnapshot
        Advice: CaseAdvice
    }

type FieldDiff =
    {
        FieldName: BusinessField
        Before: string option
        After: string option
    }

type AdvisoryReview =
    {
        Before: CaseSnapshot option
        Proposed: CaseSnapshot
        Changes: FieldDiff list
        Context: RuntimeContext
    }

module Views =
    let vocabulary = Vocabulary.ofUnion<View> "view" TokenCasing.ScreamingSnake None
