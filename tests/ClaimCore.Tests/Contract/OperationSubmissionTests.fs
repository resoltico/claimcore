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
                "noneditable UPDATE seeds remain dormant while required fixed selections stay exact"
                (fun () ->
                    let work = (TaskDeclarations.describe "work.update").Ledger |> Option.get

                    let responsible =
                        TaskInputId.Target(InputTarget.Field BusinessField.ResponsibleActor)

                    let summary =
                        TaskInputId.Target(InputTarget.Field BusinessField.HandlingSummary)

                    for display in
                        [
                            SubmissionValue.Text "10000000-0000-4000-8000-000000000001"
                            SubmissionValue.Clear
                        ] do
                        let fixedValues =
                            SubmissionAssembly.fixedInputs
                                work
                                [ summary ]
                                (Map.ofList [ responsible, display ])

                        Expect.isEmpty
                            fixedValues
                            "Availability cannot turn an UPDATE display seed into an assertion"

                        Expect.equal
                            (SubmissionAssembly.merge
                                [ summary ]
                                fixedValues
                                (Map.ofList [ summary, SubmissionValue.Text "Changed" ]))
                            (Map.ofList [ summary, SubmissionValue.Text "Changed" ])
                            "Summary-only intent remains summary-only"

                    let registration =
                        (TaskDeclarations.describe "people.register").Ledger |> Option.get

                    let subject = TaskInputId.Target InputTarget.PrincipalSubject
                    let selected = Map.ofList [ subject, SubmissionValue.Text "fixed-subject" ]

                    Expect.equal
                        (SubmissionAssembly.fixedInputs registration [] selected)
                        selected
                        "A required immutable selection is retained")
            testCase
                "creation reference is authored and an existing binding needs a positive revision"
                (fun () ->
                    let created =
                        """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"case.open","binding":{"kind":"CASE","expectedRevision":0},"inputs":{"field:caseReference":"MAP-NEW","field:incidentDate":"2026-01-01","field:receivedDate":"2026-01-02","field:incidentCountry":"LV","field:policyNumber":"Claimant","field:insurerClaimReference":"Insurer","field:claimedAmount":"12.34","field:claimedCurrency":"EUR"}}"""

                    let submitted = decode created |> accepted

                    match submitted.Operation with
                    | Ok request ->
                        Expect.equal
                            request.Binding
                            (Binding.CaseNew Revision.zero)
                            "Creation basis"

                        Expect.equal
                            request.Inputs.Values[TaskInputId.Target(
                                                      InputTarget.Field BusinessField.CaseReference
                                                  )]
                            (InputValue.Text "MAP-NEW")
                            "Authored creation reference"
                    | other -> failtestf "Expected creation: %A" other

                    let misplaced =
                        created.Replace(
                            """"expectedRevision":0""",
                            """"target":{"kind":"REFERENCE","caseReference":"OTHER"},"expectedRevision":0"""
                        )

                    Expect.isError
                        (decode misplaced)
                        "A former binding target is refused even when every new input is present"

                    Expect.isOk
                        (decode (amend ""))
                        "The existing-target positive control admits its reviewed revision"

                    Expect.isError
                        (decode (
                            (amend "")
                                .Replace(""""expectedRevision":1""", """"expectedRevision":0""")
                        ))
                        "Existing targets cannot use a creation revision")
            testCase
                "well-typed grant target authoring yields precise issues while transport types remain closed"
                (fun () ->
                    let grant values =
                        """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.grant","binding":{"kind":"PEOPLE","actorId":"10000000-0000-4000-8000-000000000001","expectedRevision":1},"inputs":{"input:role":"CASE_READER",REPLACE}}"""
                            .Replace("REPLACE", values)

                    for values, target, violation in
                        [
                            "\"input:scope\":\"CASE\",\"input:caseId\":\"bad-id\"",
                            InputTarget.CaseId,
                            Violation.CaseIdRequired
                            "\"input:scope\":\"CASE\",\"input:caseId\":\"\"",
                            InputTarget.CaseId,
                            Violation.CaseIdRequired
                            "\"input:scope\":\"CASE\"",
                            (InputTarget.Field BusinessField.CaseReference),
                            Violation.CaseSelectionInvalid
                            "\"input:scope\":\"CASE\",\"input:caseId\":\"10000000-0000-4000-8000-000000000001\",\"field:caseReference\":\"REF\"",
                            (InputTarget.Field BusinessField.CaseReference),
                            Violation.CaseSelectionInvalid
                            "\"input:scope\":\"INSTALLATION\",\"field:caseReference\":\"REF\"",
                            (InputTarget.Field BusinessField.CaseReference),
                            Violation.CaseSelectionInvalid
                        ] do
                        match decode (grant values) |> accepted |> OperationSubmission.syntax with
                        | Some issues ->
                            let issue = InputIssues.toList issues |> List.exactlyOne

                            Expect.equal
                                issue.Target
                                target
                                "The editor can link to the affected control"

                            Expect.equal
                                issue.Violation
                                violation
                                "An authoring error has a corrective meaning"
                        | other -> failtestf "Expected authoring issue: %A" other

                    for invalid in
                        [
                            "\"input:scope\":\"CASE\",\"input:caseId\":42"
                            "\"input:scope\":\"CASE\",\"field:caseReference\":{}"
                            "\"input:scope\":\"CASE\",\"unknown\":\"REF\""
                        ] do
                        Expect.isError
                            (decode (grant invalid))
                            "Structural errors remain transport refusals")
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
            testCase "received can be cleared using its declared token" (fun () ->
                let submission = decode (amend "\"field:receivedDate\":null") |> accepted
                let operation = submission.Operation |> accepted
                let key = TaskInputId.Target(InputTarget.Field BusinessField.ReceivedDate)

                Expect.equal
                    (Map.tryFind key operation.Inputs.Values)
                    (Some InputValue.Null)
                    "Explicit clearing")
            testCase
                "omitted updates keep while declared clearable inputs retain explicit null"
                (fun () ->
                    let submission = decode (amend "\"field:policyNumber\":null") |> accepted

                    let operation = submission.Operation |> accepted

                    Expect.equal
                        operation.Inputs.Values
                        (Map.ofList
                            [
                                TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber),
                                InputValue.Null
                            ])
                        "Only the explicit null changes; other inputs remain omitted")
            testCase "input key order does not change the typed canonical-intent input" (fun () ->
                let first =
                    decode (
                        amend
                            "\"field:policyNumber\":\"PN-1\",\"field:insurerClaimReference\":\"Name\""
                    )
                    |> accepted
                    |> fun submission -> submission.Operation

                let second =
                    decode (
                        amend
                            "\"field:insurerClaimReference\":\"Name\",\"field:policyNumber\":\"PN-1\""
                    )
                    |> accepted
                    |> fun submission -> submission.Operation

                Expect.equal first second "Map ordering is not authored intent")
            testCase "grant-row issues retain the submitted ordinal" (fun () ->
                let text =
                    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.admit","binding":{"kind":"PEOPLE","expectedRevision":0},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example/","input:principal.subject":"person","input:profile.displayName":"Name","input:grants":[{"input:role":"CASE_READER","input:scope":"INSTALLATION"},{"input:role":null,"input:scope":"INSTALLATION"}]}}"""

                match decode text |> accepted |> fun submission -> submission.Operation with
                | Error(Refusal.InvalidInput issues) ->
                    let issue = InputIssues.toList issues |> List.exactlyOne

                    Expect.equal
                        issue.Target
                        InputTarget.Role
                        "The row member is addressed directly"

                    Expect.equal
                        (issue.Row |> Option.map TaskRowTarget.ordinal)
                        (Some 1)
                        "The second submitted row is ordinal one"
                | other -> failtestf "Expected a row issue: %A" other)
            testCase "an offline draft requires real metadata before becoming executable" (fun () ->
                let draft =
                    {
                        SubmissionEnvelope.OperationId = None
                        Task = (TaskId.ofLiteral "case.amendRegistration")
                        Binding =
                            OperationBinding.Case(CaseSelector.Reference "MAP-1", None, None, None)
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
                                    None,
                                    None
                                )
                        })
                    "Real metadata completes the draft")
            testCase "empty authored values and paired exact money survive map assembly" (fun () ->
                let submission =
                    decode (
                        amend
                            "\"field:policyNumber\":\"\",\"field:incidentDescription\":null,\"field:claimedAmount\":\"12.3400\",\"field:claimedCurrency\":\"EUR\""
                    )
                    |> accepted

                let operation = submission.Operation |> accepted

                Expect.equal
                    operation.Inputs.Values
                    (Map.ofList
                        [
                            TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber),
                            InputValue.Text ""
                            TaskInputId.Target(InputTarget.Field BusinessField.IncidentDescription),
                            InputValue.Null
                            TaskInputId.Target(InputTarget.Field BusinessField.ClaimedAmount),
                            InputValue.Text "12.3400"
                            TaskInputId.Target(InputTarget.Field BusinessField.ClaimedCurrency),
                            InputValue.Text "EUR"
                        ])
                    "Omitted facts keep, null clears, authored text remains exact"

                Expect.isSome
                    (OperationSubmission.syntax submission)
                    "Core diagnoses empty authored name")
            testCase "sibling declaration issues retain both submitted row ordinals" (fun () ->
                let raw =
                    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.admit","binding":{"kind":"PEOPLE","expectedRevision":0},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example/","input:principal.subject":"person","input:profile.displayName":"Name","input:grants":[{"input:role":null,"input:scope":"INSTALLATION"},{"input:role":null,"input:scope":"INSTALLATION"}]}}"""

                match (decode raw |> accepted).Operation with
                | Error(Refusal.InvalidInput issues) ->
                    Expect.equal
                        (InputIssues.toList issues
                         |> List.map (fun issue ->
                             issue.Target, issue.Row |> Option.map TaskRowTarget.ordinal))
                        [ InputTarget.Role, Some 0; InputTarget.Role, Some 1 ]
                        "Identical sibling failures retain distinct ordinals"
                | other -> failtestf "%A" other)
            testCase "semantic grant failures retain sibling ordinals in DATA and HUMAN" (fun () ->
                let raw =
                    """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.admit","binding":{"kind":"PEOPLE","expectedRevision":0},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example/","input:principal.subject":"person","input:profile.displayName":"Name","input:grants":[{"input:role":"CASE_READER","input:scope":"CASE","field:caseReference":""},{"input:role":"CASE_READER","input:scope":"CASE","field:caseReference":""}]}}"""

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
                Expect.isSome
                    (decode (amend "\"field:claimedAmount\":\"1.00\"")
                     |> accepted
                     |> OperationSubmission.syntax)
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
            testCase
                "reviewed People and erasure preconditions are required exact integers"
                (fun () ->
                    let person expected =
                        """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.disable","binding":{"kind":"PEOPLE","actorId":"10000000-0000-7000-8000-000000000001"""
                        + "\""
                        + expected
                        + """},"inputs":{}}"""

                    let erase expected =
                        """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"case.erase","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-1"},"expectedRevision":1"""
                        + expected
                        + """},"inputs":{}}"""

                    for value in
                        [
                            ""
                            ",\"expectedRevision\":null"
                            ",\"expectedRevision\":0"
                            ",\"expectedRevision\":-1"
                            ",\"expectedRevision\":9007199254740992"
                            ",\"expectedRevision\":1.0"
                        ] do
                        Expect.isError
                            (decode (person value))
                            "Existing-person preconditions never mean current by omission"

                    Expect.isOk
                        (decode (person ",\"expectedRevision\":1"))
                        "Independent reviewed account input"

                    for value in
                        [
                            ""
                            ",\"expectedFamilySequence\":null"
                            ",\"expectedFamilySequence\":0"
                            ",\"expectedFamilySequence\":9007199254740992"
                            ",\"expectedFamilySequence\":1e0"
                        ] do
                        Expect.isError
                            (decode (erase value))
                            "Erasure requires a positive exact family sequence"

                    Expect.isOk
                        (decode (erase ",\"expectedFamilySequence\":2"))
                        "Independent reviewed family input")
            testCase
                "typed offline APIs preserve supplied People and erasure preconditions"
                (fun () ->
                    let id = System.Guid.Parse "019aa6a0-0000-7000-8000-000000000001"

                    let submitted task binding =
                        OperationSubmission.admit
                            {
                                SubmissionEnvelope.OperationId = id
                                Task = TaskId.ofLiteral task
                                Binding = binding
                                Inputs = Map.empty
                            }
                        |> Result.defaultWith (fun () -> failtest "Valid envelope shape")

                    let person revision =
                        submitted "people.enable" (OperationBinding.People(Some id, revision))

                    let case family =
                        submitted
                            "case.erase"
                            (OperationBinding.Case(
                                CaseSelector.Reference "TYPED",
                                1L,
                                None,
                                family
                            ))

                    for value in [ -1L; 0L; 9007199254740992L ] do
                        Expect.isSome
                            (OperationSubmission.syntax (person value))
                            "Invalid People metadata is retained"

                        Expect.isSome
                            (OperationSubmission.syntax (case (Some value)))
                            "Invalid family metadata is retained"

                        let envelope =
                            {
                                SubmissionEnvelope.OperationId = id
                                Task = (TaskId.ofLiteral "people.enable")
                                Binding = OperationBinding.People(Some id, value)
                                Inputs = Map.empty
                            }

                        let submitted =
                            OperationSubmission.admit envelope
                            |> Result.defaultWith (fun () -> failtest "Typed envelope")

                        Expect.isSome
                            (OperationSubmission.syntax submitted)
                            "ActorPeople syntax checks reviewed revision"

                        let personDraft =
                            {
                                SubmissionEnvelope.OperationId = None
                                Task = (TaskId.ofLiteral "people.enable")
                                Binding = OperationBinding.People(Some id, Some value)
                                Inputs = Map.empty
                            }

                        Expect.isSome
                            (OperationSubmission.syntaxDraft personDraft
                             |> Result.defaultWith (fun _ -> failtest "People draft syntax"))
                            "Supplied draft People revision is checked"

                        let draft =
                            {
                                SubmissionEnvelope.OperationId = None
                                Task = (TaskId.ofLiteral "case.erase")
                                Binding =
                                    OperationBinding.Case(
                                        CaseSelector.Reference "TYPED",
                                        None,
                                        None,
                                        Some value
                                    )
                                Inputs = Map.empty
                            }

                        Expect.isSome
                            (OperationSubmission.syntaxDraft draft
                             |> Result.defaultWith (fun _ -> failtest "Draft syntax"))
                            "Supplied draft family metadata is checked"

                    Expect.isNone
                        (OperationSubmission.syntax (person 1L))
                        "Positive existing revision"

                    Expect.isNone
                        (OperationSubmission.syntax (case (Some 1L)))
                        "Positive family basis"

                    Expect.isError
                        (OperationSubmission.admit
                            {
                                SubmissionEnvelope.OperationId = id
                                Task = TaskId.ofLiteral "case.erase"
                                Binding =
                                    OperationBinding.Case(
                                        CaseSelector.Reference "TYPED",
                                        1L,
                                        None,
                                        None
                                    )
                                Inputs = Map.empty
                            })
                        "Complete erasure requires its basis"

                    let creation =
                        ClaimCore.TestSupport.Submissions.create
                            "people.register"
                            id
                            (Binding.PeopleNew Revision.zero)
                            (Map.fold
                                (fun values key value -> Map.add key value values)
                                (ClaimCore.TestSupport.Submissions.principal (
                                    PrincipalKey.human "https://issuer.example/" "person"
                                    |> accepted
                                ))
                                (ClaimCore.TestSupport.Submissions.profile
                                    {
                                        DisplayName = "Person"
                                        DigestEmail = None
                                        Contact = None
                                    }))

                    Expect.isNone
                        (OperationSubmission.syntax creation)
                        "Creation zero remains valid"

                    for taskName, binding in
                        [
                            "people.register", OperationBinding.People(None, 1L)
                            "case.open", OperationBinding.CaseNew 1L
                        ] do
                        let declaration =
                            ClaimCore.Domain.Tasks.Registry.findDeclaration (
                                TaskId.ofLiteral taskName
                            )
                            |> Option.get

                        match OperationSubmission.admitBinding declaration binding with
                        | Error(SubmissionProblem.Rejected(Refusal.InvalidInput issues)) ->
                            Expect.equal
                                (InputIssues.toList issues
                                 |> List.map (fun issue -> issue.Target, issue.Violation))
                                [ InputTarget.ExpectedRevision, Violation.ExpectedRevisionRange ]
                                "Creation revisions must be exactly zero before intent encoding"
                        | other -> failtestf "Nonzero creation binding was admitted: %A" other

                    let draft =
                        {
                            SubmissionEnvelope.OperationId = None
                            Task = (TaskId.ofLiteral "case.erase")
                            Binding =
                                OperationBinding.Case(
                                    CaseSelector.Reference "TYPED",
                                    None,
                                    None,
                                    None
                                )
                            Inputs = Map.empty
                        }

                    Expect.equal
                        (OperationSubmission.syntaxDraft draft)
                        (Ok None)
                        "Draft omission permits explicit acquisition")
        ]
