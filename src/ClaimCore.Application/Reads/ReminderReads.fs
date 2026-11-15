namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal ReminderReads =
    let pendingReminderDue (tx: ReadTx) (parent, excluded) =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ("SELECT "
                     + DatabaseSession.dateExpression "due"
                     + " FROM claimcore.reminders WHERE case_id=$1 AND state='PENDING' AND ($2::uuid IS NULL OR reminder_id<>$2)")
                    [|
                        box (CaseId.value parent)
                        DatabaseSession.parameter (excluded |> Option.map ReminderId.value)
                    |]
                    token

            return rows |> List.map (DatabaseSession.calendarDate 0 >> CalendarDate.ofDate)
        }

    let findReminder (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken
        WorkStorage.findReminder connection id token

    let reminderPage (tx: ReadTx) (query: WorkStorage.PageQuery) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        WorkStorage.page connection query token
