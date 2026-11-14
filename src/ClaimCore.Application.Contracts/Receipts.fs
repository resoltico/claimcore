namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System


type DisclosedCaseReceipt =
    {
        State: LiveCase
        RecordedFields: BusinessField list
        Payment: PaymentDifference option
    }

[<RequireQualifiedAccess>]
type SnapshotDisclosure =
    | Disclosed of DisclosedCaseReceipt
    | Withheld
    | Erased

type CaseReceipt =
    {
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        Act: CaseActKind
        Snapshot: SnapshotDisclosure
    }

type PeopleReceiptEffect =
    {
        Act: PeopleActKind
        Id: EffectId
        Position: EffectPosition
    }

[<RequireQualifiedAccess>]
type PeopleTargetDisclosure =
    | Disclosed of ActorRef
    | Withheld of ActorId

module PeopleTargetDisclosure =
    let actorId =
        function
        | PeopleTargetDisclosure.Disclosed actor -> actor.ActorId
        | PeopleTargetDisclosure.Withheld actor -> actor

type PeopleReceipt =
    {
        Effects: PeopleReceiptEffect list
        TargetActor: PeopleTargetDisclosure
        Act: PeopleActKind
    }

/// Historical receipt bounds follow the retained operation format, not current authoring admission.
module PeopleReceipt =
    [<Literal>]
    let MaximumEffects = 50

[<RequireQualifiedAccess>]
type WorkDisclosure =
    | Disclosed of CaseWorkReadModel
    | Withheld
    | Erased

type WorkReceipt =
    {
        Reference: CaseReference
        CaseId: CaseId
        Revision: Revision
        Act: WorkActKind
        Snapshot: WorkDisclosure
    }

[<RequireQualifiedAccess>]
type ReminderDisclosure =
    | Disclosed of Reminder
    | Withheld
    | Erased

type ReminderVersion =
    {
        ReminderId: ReminderId
        Revision: Revision
        Snapshot: ReminderDisclosure
    }

[<RequireQualifiedAccess>]
type ReminderReceiptEffects =
    | Single of ReminderVersion
    | CompletedAndPlanned of completed: ReminderVersion * next: ReminderVersion

type ReminderReceipt =
    {
        Reference: CaseReference
        CaseId: CaseId
        Act: ReminderActKind
        Effects: ReminderReceiptEffects
    }

[<RequireQualifiedAccess>]
type NoteDisclosure =
    | Disclosed of Note
    | Withheld
    | Erased

type NoteReceipt =
    {
        Reference: CaseReference
        CaseId: CaseId
        NoteId: NoteId
        Act: NoteActKind
        Snapshot: NoteDisclosure
    }

[<RequireQualifiedAccess>]
type ReceiptSubject =
    | Case of CaseReceipt
    | People of PeopleReceipt
    | Work of WorkReceipt
    | Reminder of ReminderReceipt
    | Note of NoteReceipt

type OperationReceipt =
    {
        OperationId: OperationId
        Position: LedgerPosition
        Subject: ReceiptSubject
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Basis: WorkDateBasis
    }
