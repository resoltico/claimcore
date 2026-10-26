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

[<RequireQualifiedAccess>]
type HistoryDetail =
    | Summary
    | Full

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

module HistoryDetails =
    let vocabulary =
        Vocabulary.define "historyDetail" [ HistoryDetail.Summary; HistoryDetail.Full ] (function
            | HistoryDetail.Summary -> "SUMMARY"
            | HistoryDetail.Full -> "FULL")

module Views =
    let vocabulary =
        Vocabulary.define "view" [ View.Cases; View.Authority; View.Operations ] (function
            | View.Cases -> "CASES"
            | View.Authority -> "AUTHORITY"
            | View.Operations -> "OPERATIONS")
