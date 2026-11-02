namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.VerificationChecks

/// New projections and their derived case facets are checked against the causal ledger fold.
module internal WorkProjectionVerification =
    let verify connection installation (state: LedgerVerification.Scan) token =
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

            let! cases =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases c")
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

                    do! ProjectionVerification.workRows state.Work work
                    do! ProjectionVerification.reminderRows state.Reminders reminders

                    let! records =
                        VerificationRows.capture
                            None
                            VerificationFinding.CaseWorkProjection
                            (fun () -> cases |> List.map (Projections.caseRow installation))

                    return! ProjectionVerification.workIndexes state.Work state.Reminders records
                }
        }
