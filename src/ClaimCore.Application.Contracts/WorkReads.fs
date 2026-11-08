namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

type ReminderReadModel =
    {
        Reminder: Reminder
        Reference: CaseReference
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Acts: ReminderActKind list
        Active: bool
        Timing: ReminderDue option
    }

type ReminderRead =
    {
        Basis: WorkDateBasis
        Item: ReminderReadModel
    }

type ReminderPage =
    {
        Basis: WorkDateBasis
        Items: ReminderReadModel list
        NextCursor: string option
    }
