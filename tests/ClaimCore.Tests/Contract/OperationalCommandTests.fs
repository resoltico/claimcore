module ClaimCore.Tests.OperationalCommandTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private principal kind =
    PrincipalKey.ofKind kind "https://issuer.example/" "shared-subject"
    |> Result.toOption
    |> Option.get

let private requestPage key =
    AccessRequestsScreen.document
        {
            Basis = DomainInputs.workBasis
            NextCursor = None
            Items =
                [
                    {
                        Principal = key
                        FirstSeen = DomainInputs.workBasis.ObservedAt
                        LastSeen = DomainInputs.workBasis.ObservedAt
                        Claims = RequesterClaims.empty
                    }
                ]
        }

let private rawRequests key =
    RepresentationReply.Human
        {
            HumanReadReply.Outcome = HumanReadOutcome.Succeeded(requestPage key)
        }
    |> Codec.encode Api.peopleRequests.Output
    |> Encoding.UTF8.GetString

let private readRequests (text: string) =
    let raw =
        StrictJson.read Limits.MaximumResponseBytes (Encoding.UTF8.GetBytes text)
        |> Result.toOption
        |> Option.get

    (Api.forActor (ActorEndpoint.Query QueryTaskId.PeopleRequests)).ReadOutput raw

[<Tests>]
let tests =
    testList
        "typed operational command controls"
        [
            testCase
                "dismissal survives additional commands and round-trips exact principal kind"
                (fun () ->
                    for kind in [ PrincipalKind.Human; PrincipalKind.Service ] do
                        let key = principal kind

                        Expect.isOk
                            (readRequests (rawRequests key))
                            "Valid principal and complete task/control association"

                        let controls = requestPage key |> fun doc -> Screen.affordances doc.Screen

                        let dismissal =
                            controls
                            |> List.find (fun action ->
                                action.Task = TaskId.ofLiteral "people.requests.dismiss")

                        Expect.equal
                            dismissal.Control
                            (ActionControl.Command(CommandRequest.DismissAccessRequest key))
                            "Other commands cannot retarget dismissal"

                        Expect.isEmpty
                            dismissal.Seeds
                            "Commands do not reconstruct required principals from seeds")
            testCase
                "malformed dismissal principals return response admission errors without throwing"
                (fun () ->
                    let valid = rawRequests (principal PrincipalKind.Human)

                    for issuer in
                        [ ""; "https://user@issuer.example/"; "https://issuer.example/?query" ] do
                        Expect.isError
                            (readRequests (valid.Replace("https://issuer.example/", issuer)))
                            "Malformed principal is a fallible boundary error"

                    Expect.isError
                        (readRequests (valid.Replace("shared-subject", "")))
                        "Missing subject refuses")
            testCase
                "task and command association is checked structurally before serving"
                (fun () ->
                    let valid = rawRequests (principal PrincipalKind.Human)

                    let mismatch =
                        valid.Replace(
                            "\"task\":\"people.requests.dismiss\"",
                            "\"task\":\"session.terminateOwn\""
                        )

                    Expect.isError
                        (readRequests mismatch)
                        "Own-session task cannot carry dismissal principal binding"

                    let raw =
                        StrictJson.read
                            Limits.MaximumResponseBytes
                            (Encoding.UTF8.GetBytes mismatch)
                        |> Result.toOption
                        |> Option.get

                    Expect.isError
                        (Api.peopleRequests.Output.Validate "$" raw)
                        "Native structural validation rejects the association, independently of constructors")
            testCase "command declaration and endpoint sets are exact and nonledger" (fun () ->
                Expect.equal
                    (EndpointDeclarations.commands |> List.map _.Id |> Set.ofList)
                    (CommandTaskId.all |> List.map CommandTaskId.key |> Set.ofList)
                    "Every closed command has one declaration"

                for id in CommandTaskId.all do
                    let route = Api.forActor (ActorEndpoint.Command id)

                    Expect.equal
                        (Api.effect route)
                        EndpointEffect.OperationalWrite
                        "No command enters ledger replay"

                    Expect.equal
                        (Api.requiresInstallation route)
                        (route.Token <> "people.requests.dismiss"
                         && route.Token <> "people.requests.submit")
                        "Only the session commands have mandatory installation binding")
            testCase
                "session command argv routes through declared grammar without an operation identity"
                (fun () ->
                    let request = "00000000-0000-7000-8000-000000000001"
                    let installation = "10000000-0000-4000-8000-000000000002"

                    let own =
                        [
                            "session"
                            "terminate-own"
                            request
                            "--installation-id"
                            installation
                            "--dry-run"
                        ]

                    match CliInvocations.admit own with
                    | Ok(CliInvocation.Verb words) ->
                        Expect.equal words own "Multiword task shares executable session family"
                    | _ -> failtest "Session command must be a declared human verb"

                    match CliInvocations.admit [ "session"; "terminate-own"; "--help" ] with
                    | Ok(CliInvocation.CommandHelp(words, _, false)) ->
                        Expect.equal
                            words
                            [ "session"; "terminate-own" ]
                            "Declared task help wins over builtin session help"
                    | _ -> failtest "Declared session task help required"

                    let prepared =
                        Grammar.prepare
                            (fun () ->
                                failtest "Operational command must not allocate a ledger ID")
                            own
                        |> Result.toOption
                        |> Option.get

                    Expect.equal
                        (Api.id (Frames.endpoint prepared.Invocation))
                        "session.terminateOwn"
                        "Own termination endpoint"

                    Expect.isNone
                        (Frames.operationId prepared.Invocation)
                        "No fake operation identity"

                    Expect.equal
                        (Frames.admit prepared.Bytes |> Result.map Frames.installationId)
                        (Ok(Some(Guid.Parse installation)))
                        "Prepared frames round-trip the original binding"

                    Expect.isError
                        (Grammar.prepare
                            (fun () -> request)
                            [ "session"; "terminate-own"; request ])
                        "A previously supplied request ID cannot acquire a new installation"

                    let recovery =
                        Frames.terminationRecovery prepared.Invocation
                        |> Frames.encodeInvocation
                        |> Frames.admit
                        |> Result.toOption
                        |> Option.get

                    let original =
                        Frames.value Api.terminateOwnSessions prepared.Invocation |> Option.get

                    Expect.equal
                        (Frames.value Api.terminateOwnSessions recovery)
                        (Some { original with SettleOnly = Some true })
                        "Recovery keeps the original UUID and never starts a new cut"

                    Expect.equal
                        (Frames.installationId recovery)
                        (Some(Guid.Parse installation))
                        "Recovery keeps the original installation"

                    Expect.isError
                        (Frames.bindInstallation
                            (Guid.Parse "10000000-0000-4000-8000-000000000003")
                            recovery)
                        "Recovery cannot be rebound"

                    let fresh =
                        Grammar.prepare (fun () -> request) [ "session"; "terminate-own" ]
                        |> Result.toOption
                        |> Option.get

                    Expect.isNone
                        (Frames.installationId fresh.Invocation)
                        "Online preparation discovers fresh scope"

                    Expect.isError
                        (Frames.admit fresh.Bytes)
                        "The raw delivery boundary refuses a missing installation"

                    let person =
                        Grammar.prepare
                            (fun () ->
                                failtest "Operational command must not allocate a ledger ID")
                            [
                                "people"
                                "terminate-sessions"
                                "10000000-0000-4000-8000-000000000001"
                                request
                                "--installation-id"
                                installation
                                "--dry-run"
                            ]
                        |> Result.toOption
                        |> Option.get

                    Expect.equal
                        (Frames.value Api.terminatePersonSessions person.Invocation
                         |> Option.map _.ActorId)
                        (Some(Guid.Parse "10000000-0000-4000-8000-000000000001"))
                        "Exact selected person")
            testCase "operational unknown has neither a count nor an operation proof" (fun () ->
                let raw =
                    SessionTerminationCodecs.data.Write SessionTerminationResult.OutcomeUnknown

                Expect.equal
                    raw
                    (JsonValue.Object
                        [
                            "outcome",
                            JsonValue.Object [ "kind", JsonValue.String "OUTCOME_UNKNOWN" ]
                        ])
                    "Explicit nonledger uncertainty"

                let document = SessionTerminationScreen.terminationUnknown ()

                let text =
                    ViewText.renderDocument false (RenderingContext.forLanguage "en") document
                    |> String.concat "\n"

                Expect.stringContains
                    text
                    "newer sessions"
                    "Repeating is not falsely described as exact idempotent recovery")
        ]
