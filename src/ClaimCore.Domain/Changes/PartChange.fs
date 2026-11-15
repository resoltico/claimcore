namespace ClaimCore.Domain.Changes

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type AggregateReference =
    | Family of CaseId
    | Person of ActorId

[<RequireQualifiedAccess>]
type CompartmentOwner =
    | Family of CaseId
    | Profile of ActorId

/// Accepted case values keep public structure separate from private business facts.
type CaseContent =
    {
        Registration: Registration
        Finance: FinancialPosition
    }

type CaseValue =
    {
        Reference: CaseReference option
        Lifecycle: Lifecycle
        Status: CaseStatus
        Content: CaseContent option
        Disposition: CaseDisposition option
    }

type PaymentValue =
    {
        Removed: bool
        Entry: PaymentEntry option
    }

type ReminderValue =
    {
        State: ReminderState
        Plan: ReminderPlan option
    }

type NoteValue =
    { Removed: bool; Text: NoteText option }

/// Assignments are accepted values, not instructions to re-run a grant business rule.
/// A new accepting position is the enclosing part change's position.
type GrantAssignment = { Grant: ActorGrant; Active: bool }

/// A compartment reference retains presence even after its private bytes are cleared.
[<RequireQualifiedAccess>]
type ProfileValue =
    | Present of ActorProfile option
    | Absent

module ProfileValue =
    let value =
        function
        | ProfileValue.Present value -> value
        | ProfileValue.Absent -> None

    let clear =
        function
        | ProfileValue.Present _ -> ProfileValue.Present None
        | ProfileValue.Absent -> ProfileValue.Absent

type PersonValue =
    {
        Principal: PrincipalKey option
        Enabled: bool
        Grants: GrantAssignment list
        Profile: ProfileValue
    }

[<RequireQualifiedAccess>]
type PartValue =
    | Case of CaseValue
    | Payment of PaymentValue
    | Work of CaseWork option
    | Reminder of ReminderValue
    | Note of NoteValue
    | Person of PersonValue

[<RequireQualifiedAccess>]
type PartKind =
    | Case
    | Payment
    | Work
    | Reminder
    | Note
    | Person

module PartKind =
    let vocabulary = Vocabulary.ofUnion<PartKind> "part" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

module PartValue =
    let kind =
        function
        | PartValue.Case _ -> PartKind.Case
        | PartValue.Payment _ -> PartKind.Payment
        | PartValue.Work _ -> PartKind.Work
        | PartValue.Reminder _ -> PartKind.Reminder
        | PartValue.Note _ -> PartKind.Note
        | PartValue.Person _ -> PartKind.Person

    let clear =
        function
        | PartValue.Case value ->
            PartValue.Case
                { value with
                    Content = None
                    Disposition = None
                }
        | PartValue.Payment value -> PartValue.Payment { value with Entry = None }
        | PartValue.Work _ -> PartValue.Work None
        | PartValue.Reminder value -> PartValue.Reminder { value with Plan = None }
        | PartValue.Note value -> PartValue.Note { value with Text = None }
        | PartValue.Person value ->
            PartValue.Person
                { value with
                    Profile = ProfileValue.clear value.Profile
                }

/// One accepted part version. Its kind is derived from its typed value, never a competing field.
type PartChange =
    {
        Aggregate: AggregateReference
        PartId: Guid
        Revision: Revision
        NaturalKey: string option
        Value: PartValue
        Erases: CompartmentOwner option
        Stamp: SubjectStamp
    }

    member this.Kind = PartValue.kind this.Value

module PartChange =
    let successor previous next =
        Revision.value previous < Revision.Maximum && next = Revision.successor previous

    let owner change =
        match change.Aggregate with
        | AggregateReference.Family id -> CompartmentOwner.Family id
        | AggregateReference.Person id -> CompartmentOwner.Profile id

    let validErasure change =
        change.Erases |> Option.forall ((=) (owner change))
