namespace ClaimCore.Domain.Changes

open System
open ClaimCore.Domain

type CasePartDescription =
    {
        Values: ValueChange<CaseFields>
        Lifecycle: ValueChange<Lifecycle> option
        Fields: CaseFieldDifference list
        Money: CaseMoneyDifference list
        Disposition: CaseDisposition option
    }

type PaymentPartDescription =
    {
        Totals: ValueChange<PaymentTotals>
        Entry: PaymentEntry option
        Removed: ValueChange<bool> option
    }

type WorkPartDescription =
    {
        Values: ValueChange<CaseWork>
        Responsible: ValueChange<ActorId> option
        Summary: ValueChange<HandlingSummary> option
    }

type ReminderPartDescription =
    {
        Plans: ValueChange<ReminderPlan>
        Action: ValueChange<ReminderAction> option
        Due: ValueChange<CalendarDate> option
        Kind: ValueChange<ReminderKind> option
        DoneOn: ValueChange<CalendarDate> option
        State: ValueChange<ReminderState> option
    }

type NotePartDescription =
    {
        Text: ValueChange<NoteText> option
        Removed: ValueChange<bool> option
    }

type AttachmentPartDescription =
    {
        Withdrawn: bool
        Display: AttachmentDisplay option
    }

type PartyPartDescription =
    {
        Content: ValueChange<PartyContent>
        Removed: ValueChange<bool> option
    }

type GrantAssignmentChange =
    {
        Grant: ActorGrant
        Before: bool
        After: bool
    }

type PersonPartDescription =
    {
        Profiles: ValueChange<ActorProfile>
        Principal: ValueChange<PrincipalKey> option
        Enabled: ValueChange<bool> option
        ProfilePresent: ValueChange<bool> option
        DisplayName: ValueChange<PartyName> option
        Contact: ValueChange<ContactText> option
        DigestEmail: ValueChange<EmailAddress> option
        Grants: GrantAssignmentChange list
    }

[<RequireQualifiedAccess>]
type PartDescription =
    | Case of CasePartDescription
    | Payment of PaymentPartDescription
    | Work of WorkPartDescription
    | Reminder of ReminderPartDescription
    | Note of NotePartDescription
    | Attachment of AttachmentPartDescription
    | Party of PartyPartDescription
    | Link of ValueChange<RelatedCaseContent>
    | Person of PersonPartDescription

module PartDescription =
    let kind =
        function
        | PartDescription.Case _ -> PartKind.Case
        | PartDescription.Payment _ -> PartKind.Payment
        | PartDescription.Work _ -> PartKind.Work
        | PartDescription.Reminder _ -> PartKind.Reminder
        | PartDescription.Note _ -> PartKind.Note
        | PartDescription.Attachment _ -> PartKind.Attachment
        | PartDescription.Party _ -> PartKind.Party
        | PartDescription.Link _ -> PartKind.Link
        | PartDescription.Person _ -> PartKind.Person

type DescribedPart =
    {
        PartId: Guid
        Revision: Revision
        Description: PartDescription
    }

module internal Difference =
    let value before after =
        if before = after then
            None
        else
            Some
                {
                    ValueChange.Before = before
                    After = after
                }
