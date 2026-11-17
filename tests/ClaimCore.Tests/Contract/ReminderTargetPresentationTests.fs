module ClaimCore.Tests.ReminderTargetPresentationTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testList
        "case reminder navigation meaning"
        [
            testCase
                "the case workspace supplies its exact typed reference and complete case-context reminder selection"
                (fun () ->
                    let source = ResponseExamples.caseRead
                    let document = HumanCaseDocument.ofCore source

                    Expect.hasLength
                        document.Links
                        1
                        "Readable case supplies its scoped reminder navigation"

                    let navigation = document.Links |> List.exactlyOne

                    Expect.equal
                        navigation.LabelId
                        "label.caseReminders"
                        "Presentation owns the link meaning"

                    Expect.equal navigation.Relation LinkRelation.View "Read-only navigation"

                    match navigation.Link with
                    | Link.ReminderList query ->
                        Expect.equal
                            query.Target
                            (Some(
                                CaseSelector.Reference(
                                    CaseReference.format source.Snapshot.Reference
                                )
                            ))
                            "Reference remains explicitly tagged, not guessed from its shape"

                        Expect.isNone query.View "No standard-view predicate is silently attached"
                        Expect.isNone query.State "All reminder states can be browsed"
                        Expect.isNone query.AfterCursor "Fresh traversal"

                        Expect.equal
                            query.IncludeInactive
                            (Some true)
                            "Closed or voided case context does not silently disappear"
                    | _ -> failtest "The supplied query must be the existing reminder list")
            testCase "erased disclosure does not supply case reminder navigation" (fun () ->
                let source = ResponseExamples.caseRead

                let erased =
                    { source with
                        Snapshot =
                            { source.Snapshot with
                                State = CaseState.Erased
                            }
                    }

                Expect.isEmpty
                    (HumanCaseDocument.ofCore erased).Links
                    "No unavailable authoring or navigation seed leaks through erasure")
        ]
