namespace ClaimCore.Domain.Changes

open System
open ClaimCore.Domain

type CasePartDescription =
    {
        Lifecycle: ValueChange<Lifecycle> option
        Fields: CaseFieldDifference list
        Money: CaseMoneyDifference list
        Disposition: CaseDisposition option
    }

type PaymentPartDescription =
    {
        Entry: PaymentEntry option
        Removed: ValueChange<bool> option
    }

type WorkPartDescription =
    {
        Responsible: ValueChange<ActorId> option
        Summary: ValueChange<HandlingSummary> option
    }

type ReminderPartDescription =
    {
        Action: ValueChange<ReminderAction> option
        Due: ValueChange<CalendarDate> option
        State: ValueChange<ReminderState> option
    }

type NotePartDescription =
    {
        Text: ValueChange<NoteText> option
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
        Principal: ValueChange<PrincipalKey> option
        Enabled: ValueChange<bool> option
        DisplayName: ValueChange<PartyName> option
        Contact: ValueChange<ContactText> option
        Grants: GrantAssignmentChange list
    }

[<RequireQualifiedAccess>]
type PartDescription =
    | Case of CasePartDescription
    | Payment of PaymentPartDescription
    | Work of WorkPartDescription
    | Reminder of ReminderPartDescription
    | Note of NotePartDescription
    | Person of PersonPartDescription

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
