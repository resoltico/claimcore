namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

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
