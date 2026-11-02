namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

type ReminderReadModel =
    {
        Reminder: Reminder
        Reference: CaseReference
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        Acts: ReminderActKind list
        Active: bool
    }

type ReminderPage =
    {
        Basis: WorkDateBasis
        Items: ReminderReadModel list
        NextCursor: string option
    }

type WorkOverview =
    {
        Basis: WorkDateBasis
        Mine: bool
        ActionableCases: int64
        MissingPlanCases: int64
        OverdueCases: int64
        OverdueReminders: int64
        UnassignedCases: int64
        UnavailableAssigneeCases: int64
        AttentionCases: int64
    }

[<RequireQualifiedAccess>]
type ActivitySubject =
    | Case of Revision * CaseActKind
    | Work of Revision * WorkActKind
    | Reminder of ReminderActKind * ReminderId * Revision * (ReminderId * Revision) option

type CaseActivity =
    {
        OperationId: OperationId
        Position: LedgerPosition
        Subject: ActivitySubject
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        Detail: OperationReceipt option
    }

type CaseActivityPage =
    {
        CaseId: CaseId
        Reference: CaseReference
        Items: CaseActivity list
        NextCursor: string option
    }
