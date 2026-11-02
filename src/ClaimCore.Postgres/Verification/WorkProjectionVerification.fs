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
                    ($"SELECT {Projections.caseColumns},work_search_text FROM claimcore.cases c")
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

                    do! ProjectionVerification.workIndexes state.Work state.Reminders records

                    return!
                        cases
                        |> List.fold
                            (fun checkedRows row ->
                                checkedRows
                                |> Result.bind (fun () ->
                                    result {
                                        let! record =
                                            VerificationRows.capture
                                                None
                                                VerificationFinding.CaseWorkProjection
                                                (fun () -> Projections.caseRow installation row)

                                        let expected =
                                            state.Work
                                            |> Map.tryFind record.CaseId
                                            |> Option.bind _.Snapshot
                                            |> Option.bind _.Work.Summary
                                            |> Option.map (
                                                HandlingSummary.format >> CaseSearch.normalize
                                            )

                                        do!
                                            require
                                                None
                                                VerificationFinding.CaseWorkProjection
                                                (DatabaseSession.namedOptional<string>
                                                    "work_search_text"
                                                    row
                                                    =
                                                    expected)
                                    }))
                            (Ok())
                }
        }
