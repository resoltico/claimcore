namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.VerificationChecks

/// New projections and their derived case facets are checked against the causal ledger fold.
module internal WorkProjectionVerification =
    let verify connection (state: Replay.Scan) workIndexes token =
        task {
            let! work =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {WorkStorage.workColumns} FROM claimcore.case_work")
                    [||]
                    token

            let! reminders =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {WorkStorage.reminderColumns} FROM claimcore.reminders r")
                    [||]
                    token

            let! notes =
                DatabaseSession.namedRows
                    connection
                    ("SELECT " + NoteStorage.columns + NoteStorage.source)
                    [||]
                    token

            return
                result {
                    let! work =
                        VerificationRows.capture
                            None
                            VerificationFinding.CaseWorkProjection
                            (fun () -> work |> List.map WorkStorage.workRow)

                    let! reminders =
                        VerificationRows.capture
                            None
                            VerificationFinding.CaseWorkProjection
                            (fun () -> reminders |> List.map WorkStorage.reminderRow)

                    let! notes =
                        VerificationRows.capture
                            None
                            VerificationFinding.CaseNotesProjection
                            (fun () -> notes |> List.map NoteStorage.row)

                    do! ProjectionVerification.noteRows state.Notes notes
                    do! ProjectionVerification.workRows state.Work work
                    do! ProjectionVerification.reminderRows state.Reminders reminders

                    return!
                        ProjectionVerification.workIndexes state.Work state.Reminders workIndexes
                }
        }
