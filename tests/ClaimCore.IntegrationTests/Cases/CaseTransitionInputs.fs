module internal ClaimCore.TestSupport.CaseTransitionInputs

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.TestSupport

let closed =
    Map.ofList [ Submissions.field BusinessField.Outcome, SubmissionValue.Text "OTHER" ]

let reopened =
    Map.ofList
        [
            Submissions.input InputTarget.Reason, SubmissionValue.Text "Synthetic work resumed"
        ]
