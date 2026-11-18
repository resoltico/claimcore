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
        Handling: Handling
        Content: CaseContent option
        Disposition: CaseDisposition option
        ReopenReason: Reason option
    }

    member this.Status = Handling.status this.Handling

type PaymentValue =
    {
        Removed: bool
        Entry: PaymentEntry option
    }

type NoteValue =
    { Removed: bool; Text: NoteText option }

type PartyValue =
    {
        Removed: bool
        Content: PartyContent option
    }

type LinkValue =
    {
        Removed: bool
        Content: RelatedCaseContent option
    }

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
    | Party of PartyValue
    | Link of LinkValue
    | Person of PersonValue

module PartValue =
    let kind =
        function
        | PartValue.Case _ -> PartKind.Case
        | PartValue.Payment _ -> PartKind.Payment
        | PartValue.Work _ -> PartKind.Work
        | PartValue.Reminder _ -> PartKind.Reminder
        | PartValue.Note _ -> PartKind.Note
        | PartValue.Party _ -> PartKind.Party
        | PartValue.Link _ -> PartKind.Link
        | PartValue.Person _ -> PartKind.Person

    let clear =
        function
        | PartValue.Case value ->
            PartValue.Case
                { value with
                    Content = None
                    Disposition = None
                    Handling = Handling.clearNote value.Handling
                    ReopenReason = None
                }
        | PartValue.Payment value -> PartValue.Payment { value with Entry = None }
        | PartValue.Work _ -> PartValue.Work None
        | PartValue.Reminder value -> PartValue.Reminder(ReminderValue.Unavailable value.State)
        | PartValue.Note value -> PartValue.Note { value with Text = None }
        | PartValue.Party value -> PartValue.Party { value with Content = None }
        | PartValue.Link value -> PartValue.Link { value with Content = None }
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

    /// Accepted-value views preserve unavailable private content instead of fabricating empty state.
    let caseSnapshot reference change =
        match change.Aggregate, change.Value with
        | AggregateReference.Family id, PartValue.Case value ->
            let reference = defaultArg value.Reference reference

            let state =
                match value.Lifecycle, value.Content with
                | Lifecycle.Erased, None -> Some CaseState.Erased
                | lifecycle, Some content when lifecycle <> Lifecycle.Erased ->
                    Some(
                        CaseState.ofLifecycle
                            lifecycle
                            {
                                CaseReference = reference
                                Handling = value.Handling
                                Registration = content.Registration
                                Finance = content.Finance
                            }
                    )
                | _ -> None

            state
            |> Option.map (fun state ->
                {
                    CaseSnapshot.CaseId = id
                    Reference = reference
                    Revision = change.Revision
                    State = state
                })
        | _ -> None

    let workSnapshot change =
        match change.Aggregate, change.Value with
        | AggregateReference.Family id, PartValue.Work(Some work) ->
            Some
                {
                    CaseWorkSnapshot.CaseId = id
                    Revision = change.Revision
                    Work = work
                }
        | _ -> None

    let reminderSnapshot change =
        match change.Aggregate, change.Value with
        | AggregateReference.Family id, PartValue.Reminder value ->
            ReminderId.create change.PartId
            |> Option.bind (fun reminder -> Reminder.ofValue reminder id change.Revision value)
        | _ -> None

    let noteSnapshot change =
        match change.Aggregate, change.Value with
        | AggregateReference.Family id, PartValue.Note value when value.Removed || value.Text.IsSome ->
            NoteId.create change.PartId
            |> Option.map (fun note -> Note.ofAccepted note id value.Text)
        | _ -> None

    let paymentDifference change =
        match change.Value with
        | PartValue.Payment value ->
            value.Entry
            |> Option.map (
                if value.Removed then
                    PaymentDifference.Removed
                else
                    PaymentDifference.Recorded
            )
        | _ -> None

    let disposition change =
        match change.Value with
        | PartValue.Case value -> value.Disposition
        | _ -> None
