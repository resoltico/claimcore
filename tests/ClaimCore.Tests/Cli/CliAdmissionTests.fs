module ClaimCore.Tests.CliAdmissionTests

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
        "authority"
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
    let value = prepared tail |> Fixtures.accepted

    match Frames.value Api.operationExecute value.Invocation with
    | Some(OperationInput.Authority { Act = AuthorityAct.Admit admission }) -> admission
    | other -> failtestf "Expected a typed ADMIT operation: %A" other

[<Tests>]
let tests =
    testList
        "ADMIT collection CLI"
        [
            testCase "actor name search is a thin catalog adapter" (fun () ->
                let value =
                    Grammar.prepare
                        identity
                        [ "authority"; "list"; "--text"; "ĀDAMS عادل"; "--limit"; "7" ]
                    |> Fixtures.accepted

                let request = Frames.value Api.authorityList value.Invocation |> Option.get

                Expect.equal
                    request.Text
                    (Some "ĀDAMS عادل")
                    "The CLI preserves authored search text; the core normalizes it"

                Expect.equal request.Limit 7 "Normal pagination remains available"

                Expect.equal
                    (Encoding.UTF8.GetString value.Bytes)
                    "{\"endpoint\":\"authority.list\",\"input\":{\"text\":\"ĀDAMS عادل\",\"limit\":7}}"
                    "The wire codec defines member order"

                Expect.stringContains
                    (CommandGrammar.synopsis (TaskId.Query QueryTaskId.AuthorityList))
                    "--text VALUE"
                    "Help uses the actual grammar")
            testCase
                "irreversible confirmation is a local option and never changes wire bytes"
                (fun () ->
                    for words, target in
                        [
                            [ "case"; "erase"; "CASE-X"; "--expected-revision"; "7" ], "CASE-X"
                            [
                                "authority"
                                "erase-profile"
                                "--principal-kind"
                                "human"
                                "--issuer"
                                "https://id.example/"
                                "--subject"
                                "person"
                            ],
                            identity ()
                        ] do
                        let plain = Grammar.prepare identity words |> Fixtures.accepted

                        let confirmed =
                            Grammar.prepare identity (words @ [ "--confirm"; target ])
                            |> Fixtures.accepted

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
                            (CliConfirmation.irreversibleTarget confirmed.Invocation)
                            (Some target)
                            "Target semantics are explicit"

                    Expect.isError
                        (Grammar.prepare identity [ "case"; "list"; "--confirm"; "anything" ])
                        "Ordinary reads do not accept an irrelevant confirmation")
            testCase "profile issuer is explicit in the actual prepared admission frame" (fun () ->
                let withoutIssuer =
                    [
                        "authority"
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

                let prepared = Grammar.prepare identity words |> Fixtures.accepted

                match Frames.value Api.operationExecute prepared.Invocation with
                | Some(OperationInput.Authority request) ->
                    Expect.equal
                        request.Target.Issuer
                        "https://profile.example/"
                        "The wire principal is explicit; the server never infers it"
                | other -> failtestf "Expected authority input: %A" other)
            testCase "explicit issuer and structured modes bypass profile defaulting" (fun () ->
                let profile = Map.ofList [ CliSetting.Issuer, "https://profile.example/" ]

                for issuer in
                    [
                        [ "--issuer"; "https://explicit.example/" ]
                        [ "--issuer=https://explicit.example/" ]
                    ] do
                    let invocation = CliInvocation.Verb([ "authority"; "admit" ] @ issuer)

                    Expect.equal
                        (CliInvocations.principalIssuer profile invocation)
                        invocation
                        "Explicit input wins even before admission"

                for invocation in
                    [
                        CliInvocation.Call
                        CliInvocation.Session
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
                        value.Profile
                        {
                            DisplayName = "Authored name"
                            Contact = Some "Contact"
                        }
                        "Human profile aliases preserve their values"

                    Expect.equal
                        value.Grants
                        [
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.Installation
                            }
                            {
                                Role = Role.CaseHandler
                                Scope = GrantTarget.CaseReference "CASE@SECOND@THIRD"
                            }
                            {
                                Role = Role.Auditor
                                Scope = GrantTarget.CaseReference uuid
                            }
                        ]
                        "Only the first @ is syntax; a UUID-looking reference is never treated as an ID")
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
                    value.Grants.Length
                    3
                    "The argv adapter does not normalize away repeated rows"

                Expect.equal
                    value.Grants[0]
                    value.Grants[1]
                    "The core receives both duplicate proposals"

                Expect.equal
                    value.Grants[2].Scope
                    (GrantTarget.CaseReference "CASE-1")
                    "Applicability is not redefined by the CLI")
            testCase
                "collection bounds are enforced at preparation without a partial operation"
                (fun () ->
                    let roles n =
                        List.replicate n [ "--role"; "CASE_READER" ] |> List.concat

                    Expect.equal
                        (admission (roles 48)).Grants.Length
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
                let operation = TaskId.Operation(ActKind.Authority AuthorityKind.Admit)
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
                        Discovery.commandHelp language [ "authority"; "admit" ] |> Option.get

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
        ]
