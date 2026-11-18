module ClaimCore.Tests.InputIssueBudgetTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.Wire
open ClaimCore.Json

[<Tests>]
let tests =
    testCase
        "the issue envelope carries maximum-size admission failures and rejects excess issues"
        (fun () ->
            let request =
                Submissions.create
                    "people.admit"
                    Guid.Empty
                    (Binding.PeopleNew Revision.zero)
                    (Map.ofList
                        [
                            Submissions.input InputTarget.PrincipalKind,
                            SubmissionValue.Text "HUMAN"
                            Submissions.input InputTarget.PrincipalIssuer, SubmissionValue.Text ""
                            Submissions.input InputTarget.PrincipalSubject, SubmissionValue.Text ""
                            Submissions.input InputTarget.ProfileDisplayName,
                            SubmissionValue.Text ""
                            Submissions.input InputTarget.ProfileContact,
                            SubmissionValue.Text "\u0000"
                            Submissions.input InputTarget.Grants,
                            SubmissionValue.Rows(
                                List.replicate
                                    PeopleAdmission.MaximumGrants
                                    (Submissions.grant
                                        {
                                            Role = Role.CaseReader
                                            Scope = GrantTarget.CaseReference ""
                                        })
                            )
                        ])

            let refusal, issues =
                match OperationSubmission.syntax request with
                | Some issues -> Refusal.InvalidInput issues, InputIssues.toList issues
                | other -> failtestf "Expected accumulated invalid input, got %A" other

            let rowIssues = issues |> List.choose _.Row

            Expect.equal
                rowIssues.Length
                48
                "Each of the 48 permitted rows contributes its own failure"

            Expect.equal
                (rowIssues |> List.map TaskRowTarget.ordinal)
                [ 0..47 ]
                "Stable submitted ordinals"

            Expect.isGreaterThan
                issues.Length
                rowIssues.Length
                "Scalar failures accumulate with row failures"

            Expect.isLessThanOrEqual
                issues.Length
                DiagnosticCodecs.MaximumInputIssues
                "Actual maximum-row failure fits"

            let bytes = Codec.encode DiagnosticCodecs.diagnostic refusal

            Expect.isOk
                (Codec.decode 65536 DiagnosticCodecs.diagnostic bytes)
                "Actual failure roundtrip"

            let excessive = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
            let array = (nonNull excessive["issues"]).AsArray()
            let exemplar = (nonNull array[0]).DeepClone()
            array.Clear()

            for _ in 0 .. DiagnosticCodecs.MaximumInputIssues do
                array.Add(exemplar.DeepClone())

            Expect.isError
                (Encoding.UTF8.GetBytes(excessive.ToJsonString())
                 |> Codec.decode 65536 DiagnosticCodecs.diagnostic)
                "The transport bound remains enforced")
