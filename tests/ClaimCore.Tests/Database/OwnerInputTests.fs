module ClaimCore.Tests.OwnerInputTests

open ClaimCore.CommandLine

open System
open Expecto
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Server.Administration
open ClaimCore.Postgres
open ClaimCore.Presentation

let private flags =
    [
        "initialize"
        "Etc/UTC"
        "--owner-issuer"
        "https://issuer.example/"
        "--owner-subject"
        "human-owner"
        "--owner-name"
        "Authored owner"
    ]

let private issue args =
    match Inputs.command args with
    | Error(CommandInputFailure.Usage value) -> value.Issue
    | Error other -> failtestf "Expected a closed usage issue: %A" other
    | Ok _ -> failtest "Expected invalid initialization arguments"

let private fault args =
    match Inputs.command args with
    | Error(CommandInputFailure.Operator value) -> value
    | other -> failtestf "Expected a sanitized operator category: %A" other

[<Tests>]
let tests =
    testList
        "initial owner flags"
        [
            testCase
                "initialization accepts a human profile and allowed installation roles"
                (fun () ->
                    match
                        Inputs.command (
                            flags
                            @ [
                                "--owner-contact"
                                "owner@example.test"
                                "--owner-role"
                                "CASE_HANDLER"
                                "--owner-role"
                                "PAYMENT_RECORDER"
                            ]
                        )
                    with
                    | Ok(Initialize(zone, owner)) ->
                        Expect.equal zone "Etc/UTC" "Explicit business zone"

                        Expect.equal
                            owner.Principal
                            (PrincipalKey.human "https://issuer.example/" "human-owner"
                             |> Result.defaultWith (string >> invalidOp))
                            "Human identity"

                        Expect.equal
                            owner.Profile
                            {
                                DisplayName = "Authored owner"
                                Contact = Some "owner@example.test"
                            }
                            "Authored profile"

                        Expect.equal
                            owner.Roles
                            [ Role.CaseHandler; Role.PaymentRecorder ]
                            "Only additional roles are authored"
                    | other -> failtestf "Expected initialization: %A" other)
            testCase
                "owner flags are required without an operation identity or private principal file"
                (fun () ->
                    Expect.equal
                        (issue [ "initialize"; "Etc/UTC" ])
                        (UsageError.MissingOption "--owner-issuer")
                        "Missing owner identity"

                    Expect.equal
                        (fault (flags @ [ "--operation-id"; Guid.NewGuid().ToString("D") ]))
                        OperatorFault.InvocationInvalid
                        "Identity is minted inside initialization"

                    Expect.isTrue
                        (match Inputs.command [ "provision-owner" ] with
                         | Error _ -> true
                         | _ -> false)
                        "Retired command refuses")
            testCase "initial roles reject unsupported and duplicate grants" (fun () ->
                for role in [ "OWNER"; "CASE_READER"; "CASE_REGISTRAR"; "case-handler" ] do
                    Expect.equal
                        (fault (flags @ [ "--owner-role"; role ]))
                        OperatorFault.InitialOwnerInvalid
                        "Unsupported roles use the sanitized owner category"

                Expect.equal
                    (fault (
                        flags @ [ "--owner-role"; "CASE_HANDLER"; "--owner-role"; "CASE_HANDLER" ]
                    ))
                    OperatorFault.InitialOwnerInvalid
                    "Duplicate role is not silently discarded")
            testCase
                "profile validation uses a sanitized owner category without authored values"
                (fun () ->
                    let invalidName =
                        flags
                        |> List.map (fun value -> if value = "Authored owner" then " " else value)

                    Expect.equal
                        (fault invalidName)
                        OperatorFault.InitialOwnerInvalid
                        "Name admission is shared with actor profiles"

                    Expect.equal
                        (fault (flags @ [ "--owner-contact"; "\n" ]))
                        OperatorFault.InitialOwnerInvalid
                        "Contact admission is shared"

                    Expect.equal
                        (issue (flags @ [ "--owner-name"; "Second" ]))
                        (UsageError.DuplicateOption "--owner-name")
                        "A scalar flag has one value")
            testCase
                "maintenance is idempotent on/off syntax and retired generation flags refuse"
                (fun () ->
                    Expect.equal
                        (Inputs.command [ "maintenance"; "on" ])
                        (Ok(Maintenance ClaimCore.Application.Contracts.MaintenanceChange.On))
                        "On"

                    Expect.equal
                        (Inputs.command [ "maintenance"; "off" ])
                        (Ok(Maintenance ClaimCore.Application.Contracts.MaintenanceChange.Off))
                        "Off"

                    Expect.isTrue
                        (match Inputs.command [ "maintenance"; "resume"; "--generation"; "1" ] with
                         | Error _ -> true
                         | _ -> false)
                        "No generation contract remains")
        ]
