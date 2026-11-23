module ClaimCore.Tests.CliAdmissionTests

open ClaimCore.CommandLine

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.Application.Contracts

let private identity () = "11111111-2222-7333-8444-555555555555"

let private baseArguments =
    [
        "people"
        "admit"
        "--principal-kind"
        "HUMAN"
        "--issuer"
        "https://id.example/"
        "--subject"
        "new-person"
        "--name"
        "Authored name"
    ]

let private prepared tail =
    Grammar.prepare identity (baseArguments @ tail)

let private admission tail =
    let value = prepared tail |> CaseBuilders.accepted

    Frames.value Api.operationExecute value.Invocation
    |> Option.map (ClaimCore.TestSupport.Submissions.command >> _.Inputs)
    |> Option.defaultWith (fun () -> failtest "ADMIT input required")

let private rows (value: InputMap) =
    value.Collections[TaskCollectionId.PeopleGrants] |> List.map _.Values


[<Tests>]
let tests =
    testList
        "ADMIT collection CLI"
        [
            testCase "actor name search is a thin catalog adapter" (fun () ->
                let value =
                    Grammar.prepare
                        identity
                        [ "people"; "list"; "--text"; "ĀDAMS عادل"; "--limit"; "7" ]
                    |> CaseBuilders.accepted

                let request = Frames.value Api.peopleList value.Invocation |> Option.get

                Expect.equal
                    request.Text
                    (Some "ĀDAMS عادل")
                    "The CLI preserves authored search text; the core normalizes it"

                Expect.equal request.Limit 7 "Normal pagination remains available"

                Expect.equal
                    (Encoding.UTF8.GetString value.Bytes)
                    "{\"endpoint\":\"people.list\",\"input\":{\"text\":\"ĀDAMS عادل\",\"limit\":7}}"
                    "The wire codec defines member order"

                Expect.stringContains
                    (CommandGrammar.synopsis ((QueryTaskId.key QueryTaskId.PeopleList)))
                    "--text VALUE"
                    "Help uses the actual grammar")
            testCase "exact confirmation is a local option and never changes wire bytes" (fun () ->
                for words, target in
                    [
                        [
                            "case"
                            "erase"
                            "CASE-X"
                            "--expected-revision"
                            "7"
                            "--expected-family-sequence"
                            "12"
                        ],
                        "CASE-X"
                        [
                            "people"
                            "erase-profile"
                            "10000000-0000-4000-8000-000000000001"
                            "--expected-revision"
                            "1"
                        ],
                        identity ()
                        baseArguments @ [ "--role"; "CASE_READER" ], "new-person"
                    ] do
                    let plain = Grammar.prepare identity words |> CaseBuilders.accepted

                    let confirmed =
                        Grammar.prepare identity (words @ [ "--confirm"; target ])
                        |> CaseBuilders.accepted

                    Expect.equal
                        confirmed.Bytes
                        plain.Bytes
                        "Confirmation is not a protocol member"

                    Expect.equal
                        confirmed.Confirmation
                        (Some target)
                        "Local confirmation is retained"

                    Expect.equal
                        (CliConfirmation.validate false plain)
                        (Some(UsageError.MissingOption "--confirm"))
                        "Redirected writes require explicit confirmation"

                    Expect.equal
                        (CliConfirmation.validate false confirmed)
                        None
                        "Exact target authorizes this local step"

                    let wrong =
                        { confirmed with
                            Confirmation = Some "wrong"
                        }

                    Expect.equal
                        (CliConfirmation.validate true wrong)
                        (Some(UsageError.InvalidChoice("--confirm", "wrong", [ target ])))
                        "Mismatch is a usage error even at a terminal"

                    Expect.equal
                        (CliConfirmation.requiredTarget confirmed.Invocation
                         |> Option.map ConfirmationTarget.text)
                        (Some target)
                        "Target semantics are explicit"

                Expect.isError
                    (Grammar.prepare identity [ "case"; "list"; "--confirm"; "anything" ])
                    "Ordinary reads do not accept an irrelevant confirmation")
            testCase
                "subjects with controls and escape-looking literals have distinct exact spellings"
                (fun () ->
                    let control = "synthetic\nsubject"
                    let shown = "\"synthetic\\nsubject\""
                    let literal = shown

                    let spelling = ConfirmationTarget.principal >> ConfirmationTarget.text

                    Expect.equal
                        (spelling "service\\account")
                        "service\\account"
                        "Ordinary subjects stay verbatim"

                    Expect.equal (spelling control) shown "A line feed is typeable as quoted JSON"

                    Expect.notEqual
                        (spelling literal)
                        shown
                        "A literal quoted identifier cannot impersonate the line feed"

                    let arguments value =
                        [
                            "people"
                            "admit"
                            "--principal-kind"
                            "HUMAN"
                            "--issuer"
                            "https://id.example/"
                            "--subject"
                            control
                            "--name"
                            "Authored name"
                            "--role"
                            "CASE_READER"
                            "--confirm"
                            value
                        ]

                    let valid = Grammar.prepare identity (arguments shown) |> CaseBuilders.accepted

                    let wrong =
                        Grammar.prepare identity (arguments (spelling literal))
                        |> CaseBuilders.accepted

                    Expect.isNone
                        (CliConfirmation.validate false valid)
                        "CLI automation uses the displayed spelling"

                    Expect.isSome
                        (CliConfirmation.validate false wrong)
                        "A different subject's spelling is refused"

                    let captured = StringBuilder()

                    let console =
                        {
                            IsInteractive = true
                            ReadLine = fun () -> Some shown
                            Write =
                                fun bytes ->
                                    captured.Append(Encoding.UTF8.GetString bytes) |> ignore
                        }

                    Expect.isTrue
                        (CliConfirmation.exactConfirmation
                            console
                            "en"
                            (ConfirmationTarget.principal control))
                        "The printed challenge can be typed exactly"

                    Expect.equal
                        (captured.ToString())
                        ("Type " + shown + " to confirm: ")
                        "The challenge is printed once")
            testCase "profile issuer is explicit in the actual prepared admission frame" (fun () ->
                let withoutIssuer =
                    [
                        "people"
                        "admit"
                        "--principal-kind"
                        "HUMAN"
                        "--subject"
                        "person"
                        "--name"
                        "Authored"
                        "--role"
                        "CASE_READER"
                    ]

                let profile = Map.ofList [ CliSetting.Issuer, "https://profile.example/" ]

                let words =
                    CliInvocations.principalIssuer profile (CliInvocation.Verb withoutIssuer)
                    |> function
                        | CliInvocation.Verb words -> words
                        | _ -> failtest "Expected human verb"

                let prepared = Grammar.prepare identity words |> CaseBuilders.accepted

                match
                    (Frames.value Api.operationExecute
                     >> Option.map ClaimCore.TestSupport.Submissions.command)
                        prepared.Invocation
                with
                | Some request ->
                    Expect.equal
                        request.Inputs.Values[TaskInputId.Target InputTarget.PrincipalIssuer]
                        (InputValue.Text "https://profile.example/")
                        "Principal issuer is explicit"
                | other -> failtestf "Expected people input: %A" other)
            testCase "explicit issuer and structured modes bypass profile defaulting" (fun () ->
                let profile = Map.ofList [ CliSetting.Issuer, "https://profile.example/" ]

                for issuer in
                    [
                        [ "--issuer"; "https://explicit.example/" ]
                        [ "--issuer=https://explicit.example/" ]
                    ] do
                    let invocation = CliInvocation.Verb([ "people"; "admit" ] @ issuer)

                    Expect.equal
                        (CliInvocations.principalIssuer profile invocation)
                        invocation
                        "Explicit input wins even before admission"

                for invocation in
                    [
                        CliInvocation.Call
                        CliInvocation.Batch
                        CliInvocation.Verb [ "case"; "list" ]
                    ] do
                    Expect.equal
                        (CliInvocations.principalIssuer profile invocation)
                        invocation
                        "Unrelated modes gain no issuer argument")
            testCase
                "actual collection metadata determines installation and explicit reference rows"
                (fun () ->
                    let uuid = "00000000-0000-4000-8000-000000000001"

                    let value =
                        admission
                            [
                                "--contact"
                                "Contact"
                                "--role"
                                "case-reader"
                                "--role"
                                "CASE_HANDLER@CASE@SECOND@THIRD"
                                "--role"
                                "AUDITOR@" + uuid
                            ]

                    Expect.equal
                        value.Values[TaskInputId.Target InputTarget.ProfileDisplayName]
                        (InputValue.Text "Authored name")
                        "Name alias"

                    Expect.equal
                        value.Values[TaskInputId.Target InputTarget.ProfileContact]
                        (InputValue.Text "Contact")
                        "Contact alias"

                    Expect.equal
                        (rows value)
                        [
                            Map.ofList
                                [
                                    TaskInputId.Target InputTarget.Role,
                                    InputValue.Text "CASE_READER"
                                    TaskInputId.Scope, InputValue.Text "INSTALLATION"
                                ]
                            Map.ofList
                                [
                                    TaskInputId.Target InputTarget.Role,
                                    InputValue.Text "CASE_HANDLER"
                                    TaskInputId.Scope, InputValue.Text "CASE"
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.CaseReference
                                    ),
                                    InputValue.Text "CASE@SECOND@THIRD"
                                ]
                            Map.ofList
                                [
                                    TaskInputId.Target InputTarget.Role, InputValue.Text "AUDITOR"
                                    TaskInputId.Scope, InputValue.Text "CASE"
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.CaseReference
                                    ),
                                    InputValue.Text uuid
                                ]
                        ]
                        "Only first @ separates the explicit reference")
            testCase "duplicates and role scope business policy remain core-owned" (fun () ->
                let value =
                    admission
                        [
                            "--role"
                            "CASE_READER"
                            "--role"
                            "CASE_READER"
                            "--role"
                            "OWNER@CASE-1"
                        ]

                Expect.equal
                    (rows value).Length
                    3
                    "The argv adapter does not normalize away repeated rows"

                let proposals = rows value

                Expect.equal
                    (List.item 0 proposals)
                    (List.item 1 proposals)
                    "Core receives both duplicates"

                Expect.equal
                    (proposals
                     |> List.item 2
                     |> Map.find (
                         TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)
                     ))
                    (InputValue.Text "CASE-1")
                    "Applicability remains core-owned")
            testCase
                "collection bounds are enforced at preparation without a partial operation"
                (fun () ->
                    let roles n =
                        List.replicate n [ "--role"; "CASE_READER" ] |> List.concat

                    Expect.equal
                        (rows (admission (roles 48))).Length
                        48
                        "The full declared maximum is retained"

                    Expect.isError
                        (prepared (roles 49))
                        "The 49th row cannot produce an executable frame"

                    match prepared [] with
                    | Error issues ->
                        Expect.contains
                            issues
                            (UsageError.MissingOption "--role")
                            "The required collection has a named missing option"
                    | Ok _ -> failtest "ADMIT requires at least one role")
            testCase "discovery exposes repeated role syntax and rejects creation clears" (fun () ->
                let operation = "people.admit"
                let text = CommandGrammar.synopsis operation
                Expect.stringContains text "--name" "The profile name uses the human alias"

                Expect.stringContains
                    text
                    "--role ROLE[@CASE-REF]..."
                    "Collection syntax is part of the same grammar"

                Expect.isFalse
                    (text.Contains "--clear-contact")
                    "Creation cannot clear a profile value"

                for language in [ "en"; "lv"; "ar" ] do
                    let help =
                        Discovery.commandHelp
                            (RenderingContext.forLanguage language)
                            [ "people"; "admit" ]
                        |> Option.get

                    Expect.stringContains
                        help
                        "--role ROLE[@CASE-REF]"
                        "The real help path exposes the repeated collection"

                    Expect.stringContains help "1–48" "Bounds come from the collection producer"

                Expect.isError
                    (prepared [ "--role"; "CASE_READER"; "--clear-contact" ])
                    "Inapplicable clear is refused")
            testCase "unknown roles are usage failures and never invent grant objects" (fun () ->
                match prepared [ "--role"; "NOT_A_ROLE@CASE-1" ] with
                | Error issues ->
                    Expect.isTrue
                        (issues
                         |> List.exists (function
                             | UsageError.InvalidChoice("--role", "NOT_A_ROLE@CASE-1", _) -> true
                             | _ -> false))
                        "The entire compound token remains visible in usage errors"
                | Ok _ -> failtest "Unknown roles must be refused")
            testCase "dry-run rejects invalid reviewed People and family revisions" (fun () ->
                for words in
                    [
                        [
                            "case"
                            "erase"
                            "CASE-1"
                            "--expected-revision"
                            "1"
                            "--expected-family-sequence"
                            "0"
                            "--dry-run"
                        ]
                        [
                            "people"
                            "disable"
                            "10000000-0000-4000-8000-000000000001"
                            "--expected-revision"
                            "0"
                            "--dry-run"
                        ]
                    ] do
                    Expect.isError
                        (Grammar.prepare identity words)
                        "Dry-run must not emit an inadmissible precondition")
        ]
