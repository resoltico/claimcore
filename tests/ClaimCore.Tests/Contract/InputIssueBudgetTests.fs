module ClaimCore.Tests.InputIssueBudgetTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Json

[<Tests>]
let tests =
    testCase
        "the issue envelope carries maximum-size admission failures and rejects excess issues"
        (fun () ->
            let request: PeopleRequest =
                {
                    OperationId = Guid.Empty
                    Target =
                        {
                            Kind = PrincipalKind.Human
                            Issuer = ""
                            Subject = ""
                        }
                    Act =
                        PeopleAct.Admit
                            {
                                Profile =
                                    {
                                        DisplayName = ""
                                        Contact = Some "\u0000"
                                    }
                                Grants =
                                    List.replicate
                                        PeopleAdmission.MaximumGrants
                                        {
                                            Role = Role.CaseReader
                                            Scope = GrantTarget.CaseReference ""
                                        }
                            }
                }

            let refusal, issues =
                match People.parse request with
                | Error(Refusal.InvalidInput issues as refusal) ->
                    refusal, InputIssues.toList issues
                | value -> failtestf "Expected accumulated invalid input, got %A" value

            let rowIssues = issues |> List.choose _.GrantRow

            Expect.equal
                rowIssues.Length
                48
                "Each of the 48 permitted rows contributes its own failure"

            Expect.equal
                (rowIssues |> List.map GrantOrdinal.value)
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
