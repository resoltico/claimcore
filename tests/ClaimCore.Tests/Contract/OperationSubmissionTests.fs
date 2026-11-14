module ClaimCore.Tests.OperationSubmissionTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire

let private decode text =
    Codec.decode 65536 OperationSubmissionCodecs.submission (Encoding.UTF8.GetBytes(text: string))

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

let private amend values =
    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"case.amendRegistration","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"MAP-1"},"expectedRevision":1},"inputs":{REPLACE}}"""
        .Replace("REPLACE", values)

[<Tests>]
let tests =
    testList
        "declared operation submissions"
        [
            testCase
                "unknown and foreign authored keys are refused by the closed task map"
                (fun () ->
                    for key in [ "unknown"; "input:noteText" ] do
                        Expect.isError
                            (decode (amend ("\"" + key + "\":\"x\"")))
                            "No undeclared input reaches assembly")
            testCase "binding kind and task-specific selection members are closed" (fun () ->
                let original = amend ""

                Expect.isError
                    (decode (original.Replace("\"CASE\"", "\"WORK\"")))
                    "A foreign subject cannot supply a binding"

                Expect.isError
                    (decode (
                        original.Replace(
                            "\"expectedRevision\":1",
                            "\"expectedRevision\":1,\"paymentId\":\"10000000-0000-4000-8000-000000000001\""
                        )
                    ))
                    "Payment selection belongs only to removal")
            testCase "illegal clearing is an issue on the declared input token" (fun () ->
                let submission = decode (amend "\"field:claimantName\":null") |> accepted

                match submission.Operation with
                | Error(Refusal.InvalidInput issues) ->
                    let issue = InputIssues.toList issues |> List.exactlyOne

                    Expect.equal
                        issue.Target
                        (InputTarget.Field BusinessField.ClaimantName)
                        "The input identity is preserved"

                    Expect.equal issue.GrantRow None "Scalar issues have no row"
                | other -> failtestf "Expected an input issue: %A" other)
            testCase
                "omitted updates keep while declared clearable inputs retain explicit null"
                (fun () ->
                    let submission = decode (amend "\"field:policyNumber\":null") |> accepted

                    match submission.Operation |> accepted with
                    | SubmittedOperation.Command(OperationInput.Case request) ->
                        match request.Act with
                        | CaseAct.AmendRegistration update ->
                            Expect.equal
                                (RegistrationUpdateInput.values update)
                                [ BusinessField.PolicyNumber, None ]
                                "Only the supplied input is changed"
                        | _ -> failtest "Amendment required"
                    | _ -> failtest "Typed case command required")
            testCase "input key order does not change the typed canonical-intent input" (fun () ->
                let first =
                    decode (
                        amend "\"field:policyNumber\":\"PN-1\",\"field:claimantName\":\"Name\""
                    )
                    |> accepted
                    |> fun submission -> submission.Operation

                let second =
                    decode (
                        amend "\"field:claimantName\":\"Name\",\"field:policyNumber\":\"PN-1\""
                    )
                    |> accepted
                    |> fun submission -> submission.Operation

                Expect.equal first second "Map ordering is not authored intent")
            testCase "grant-row issues retain the submitted ordinal" (fun () ->
                let text =
                    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.admit","binding":{"kind":"PEOPLE"},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example/","input:principal.subject":"person","input:profile.displayName":"Name","input:grants":[{"input:role":"CASE_READER","input:scope":"INSTALLATION"},{"input:role":null,"input:scope":"INSTALLATION"}]}}"""

                match decode text |> accepted |> fun submission -> submission.Operation with
                | Error(Refusal.InvalidInput issues) ->
                    let issue = InputIssues.toList issues |> List.exactlyOne

                    Expect.equal
                        issue.Target
                        InputTarget.Role
                        "The row member is addressed directly"

                    Expect.equal
                        (issue.GrantRow |> Option.map GrantOrdinal.value)
                        (Some 1)
                        "The second submitted row is ordinal one"
                | other -> failtestf "Expected a row issue: %A" other)
            testCase "an offline draft requires real metadata before becoming executable" (fun () ->
                let draft =
                    {
                        SubmissionEnvelope.OperationId = None
                        Task = ActKind.Case CaseActKind.AmendRegistration
                        Binding = OperationBinding.Case(CaseSelector.Reference "MAP-1", None, None)
                        Inputs =
                            Map.ofList
                                [
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.PolicyNumber
                                    ),
                                    SubmissionValue.Text "PN-1"
                                ]
                    }

                Expect.isNone
                    (OperationSubmission.completeDraft draft)
                    "A draft has no executable defaults"

                Expect.equal
                    (OperationSubmission.syntaxDraft draft)
                    (Ok None)
                    "Authored syntax needs no invented metadata"

                let id = System.Guid.Parse "019aa6a0-0000-7000-8000-000000000001"

                Expect.isNone
                    (OperationSubmission.completeDraft { draft with OperationId = Some id })
                    "An operation ID does not invent a version"

                Expect.isSome
                    (OperationSubmission.completeDraft
                        { draft with
                            OperationId = Some id
                            Binding =
                                OperationBinding.Case(
                                    CaseSelector.Reference "MAP-1",
                                    Some 1L,
                                    None
                                )
                        })
                    "Real metadata completes the draft")
            testCase "empty authored values and paired exact money survive map assembly" (fun () ->
                let submission =
                    decode (
                        amend
                            "\"field:claimantName\":\"\",\"field:incidentDescription\":null,\"field:claimedAmount\":\"12.3400\",\"field:claimedCurrency\":\"EUR\""
                    )
                    |> accepted

                match submission.Operation |> accepted with
                | SubmittedOperation.Command(OperationInput.Case {
                                                                     Act = CaseAct.AmendRegistration update
                                                                 }) ->
                    Expect.equal
                        (RegistrationUpdateInput.values update |> Map.ofList)
                        (Map.ofList
                            [
                                BusinessField.ClaimantName, Some ""
                                BusinessField.IncidentDescription, None
                                BusinessField.ClaimedAmount, Some "12.3400"
                                BusinessField.ClaimedCurrency, Some "EUR"
                            ])
                        "Omitted facts keep, null clears, and authored text remains exact"

                    Expect.isSome
                        (OperationSubmission.syntax submission)
                        "Core diagnoses an empty authored name"
                | other -> failtestf "%A" other)
            testCase "sibling declaration issues retain both submitted row ordinals" (fun () ->
                let raw =
                    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.admit","binding":{"kind":"PEOPLE"},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example/","input:principal.subject":"person","input:profile.displayName":"Name","input:grants":[{"input:role":null,"input:scope":"INSTALLATION"},{"input:role":null,"input:scope":"INSTALLATION"}]}}"""

                match (decode raw |> accepted).Operation with
                | Error(Refusal.InvalidInput issues) ->
                    Expect.equal
                        (InputIssues.toList issues
                         |> List.map (fun issue ->
                             issue.Target, issue.GrantRow |> Option.map GrantOrdinal.value))
                        [ InputTarget.Role, Some 0; InputTarget.Role, Some 1 ]
                        "Identical sibling failures retain distinct ordinals"
                | other -> failtestf "%A" other)
            testCase "semantic grant failures retain sibling ordinals in DATA and HUMAN" (fun () ->
                let raw =
                    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.admit","binding":{"kind":"PEOPLE"},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example/","input:principal.subject":"person","input:profile.displayName":"Name","input:grants":[{"input:role":"CASE_READER","input:scope":"CASE","field:caseReference":""},{"input:role":"CASE_READER","input:scope":"CASE","field:caseReference":""}]}}"""

                let submission = decode raw |> accepted

                let issues =
                    OperationSubmission.syntax submission
                    |> Option.defaultWith (fun () -> failtest "Empty references must fail")

                let targets =
                    InputIssues.toList issues
                    |> List.map (fun issue -> TaskInputIssue.ofDomain issue)

                Expect.equal
                    (targets
                     |> List.map (fun value -> value.Row |> Option.map TaskRowTarget.ordinal))
                    [ Some 0; Some 1 ]
                    "Both identical authored errors retain their row"

                Expect.equal
                    (targets |> List.map _.Input)
                    [
                        TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)
                        TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)
                    ]
                    "Both representations address the declared token")
            testCase "partial money updates and out-of-range row targets are refused" (fun () ->
                Expect.isError
                    (decode (amend "\"field:claimedAmount\":\"1.00\""))
                    "Both coordinates of a selected money update are required"

                Expect.isNone
                    (TaskRowTarget.create TaskCollectionId.PeopleGrants -1)
                    "No negative submitted ordinal"

                Expect.isNone
                    (TaskRowTarget.create TaskCollectionId.PeopleGrants 48)
                    "The grant-row bound is enforced"

                Expect.equal
                    (TaskRowTarget.create TaskCollectionId.PeopleGrants 47
                     |> Option.map TaskRowTarget.ordinal)
                    (Some 47)
                    "The last supported row remains addressable")
        ]
