module ClaimCore.Tests.BoundaryStrictnessTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.Json
open ClaimCore.TestSupport

let private capturedArguments () =
    File.ReadAllLines(Path.Combine(RepositoryRoot.find (), "tests/fixtures/cli-commands.txt"))
    |> Array.map (fun row ->
        row.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
        |> CliExamples.complete)
    |> Array.toList

let private cliStrictness () =
    let prepare arguments =
        Grammar.prepare (fun () -> "11111111-2222-7333-8444-555555555555") arguments
        |> Result.isOk

    let host arguments =
        CliInvocations.admit arguments |> Result.isOk

    let captures = capturedArguments ()

    for operation in CommandGrammar.operations do
        let words = CommandGrammar.words operation

        Expect.isTrue
            (captures
             |> List.exists (fun arguments -> List.truncate words.Length arguments = words))
            "Every business verb has an independently captured valid invocation"

    let cases =
        (captures |> List.map (fun arguments -> arguments, prepare))
        @ ([
            [ "help" ]
            [ "version" ]
            [ "schema"; "invocation" ]
            [ "call" ]
            [ "session" ]
           ]
           |> List.map (fun arguments -> arguments, host))

    for arguments, admit in cases do
        Expect.isTrue (admit arguments) "The strictness control starts with valid syntax"

    let declared =
        CommandGrammar.operations
        |> List.collect (fun operation ->
            CommandGrammar.inputs operation
            |> List.map (fun input ->
                "--" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)))
        |> fun flags ->
            Set.ofList (
                flags
                @ [ "--help"; "--format"; "--language"; "--preview"; "--dry-run"; "--json" ]
            )

    let rejects admit arguments flag = not (admit (arguments @ [ flag ]))

    let generated =
        property {
            let! suffix = Gen.string (Range.constant 1 24) (Gen.char 'a' 'z')
            let flag = "--undeclared-" + suffix

            return
                not (Set.contains flag declared)
                && (cases |> List.forall (fun (arguments, admit) -> rejects admit arguments flag))
        }

    PropertyHarness.run "CLI undeclared flag admission" generated
    let flag = "--undeclared-flag-control"
    let arguments, admit = cases.Head

    let permissive values =
        admit (values |> List.filter ((<>) flag))

    Expect.isFalse
        (rejects permissive arguments flag)
        "The same property detects a grammar variant that accepts the extra flag"

let rec private mutations transform value =
    [
        yield! transform value
        match value with
        | JsonValue.Object fields ->
            for index, (key, child) in List.indexed fields do
                for changed in mutations transform child do
                    yield
                        JsonValue.Object(
                            fields
                            |> List.mapi (fun current field ->
                                if current = index then key, changed else field)
                        )
        | JsonValue.Array values ->
            for index, child in List.indexed values do
                for changed in mutations transform child do
                    yield
                        JsonValue.Array(
                            values
                            |> List.mapi (fun current value ->
                                if current = index then changed else value)
                        )
        | _ -> ()
    ]

let private extraMember name =
    function
    | JsonValue.Object fields -> [ JsonValue.Object(fields @ [ name, JsonValue.Null ]) ]
    | _ -> []

let private unknownKind name =
    function
    | JsonValue.Object fields ->
        if List.exists (fst >> (=) "kind") fields then
            [
                JsonValue.Object(
                    fields
                    |> List.map (fun (key, value) ->
                        key, if key = "kind" then JsonValue.String name else value)
                )
            ]
        else
            [ JsonValue.Object(fields @ [ "kind", JsonValue.String name ]) ]
    | _ -> []

let rec private stripMember name =
    function
    | JsonValue.Object fields ->
        JsonValue.Object(
            fields
            |> List.filter (fst >> (<>) name)
            |> List.map (fun (key, value) -> key, stripMember name value)
        )
    | JsonValue.Array values -> JsonValue.Array(List.map (stripMember name) values)
    | value -> value

let rec private aliasKind alias declared =
    function
    | JsonValue.Object fields ->
        JsonValue.Object(
            fields
            |> List.map (fun (key, value) ->
                key,
                if key = "kind" && value = JsonValue.String alias then
                    JsonValue.String declared
                else
                    aliasKind alias declared value)
        )
    | JsonValue.Array values -> JsonValue.Array(List.map (aliasKind alias declared) values)
    | value -> value

let private endpointStrictness () =
    let requests =
        RequestInputs.values ()
        |> List.groupBy (fst >> _.Token)
        |> List.map (fun (endpoint, values) -> endpoint, values |> List.map snd)
        |> Map.ofList

    let replies =
        Api.all
        |> List.map (fun route ->
            route.Token,
            ResponseExamples.samples route.Token
            |> List.map (fun bytes ->
                StrictJson.read Limits.MaximumResponseBytes bytes
                |> Result.defaultWith (string >> failtest)))
        |> Map.ofList

    let cases =
        Api.all
        |> List.collect (fun route ->
            [
                requests[route.Token], route.NormalizeInput
                replies[route.Token], route.NormalizeOutput
            ]
            |> List.collect (fun (seeds, normalize) ->
                Expect.isNonEmpty
                    seeds
                    "Every endpoint and direction has current typed producer controls"

                seeds
                |> List.map (fun seed ->
                    Expect.isOk (normalize seed) "A refusing mutation starts with valid input"
                    seed, normalize)))

    let refuses normalize changed =
        List.forall (normalize >> Result.isError) changed

    let generated =
        property {
            let! suffix = Gen.string (Range.constant 1 20) (Gen.char 'a' 'z')

            let memberName, kind =
                "undeclared" + suffix, "SYNTHETIC_" + suffix.ToUpperInvariant()

            return
                cases
                |> List.forall (fun (seed, normalize) ->
                    refuses normalize (mutations (extraMember memberName) seed)
                    && refuses normalize (mutations (unknownKind kind) seed))
        }

    PropertyHarness.run "endpoint member and kind strictness" generated

    let seed =
        requests[Api.operationExecute.Token]
        |> List.find (fun value ->
            JsonValue.tryMember "task" value = Some(JsonValue.String "case.open"))

    let memberName, kind = "undeclaredControl", "SYNTHETIC_KIND_CONTROL"
    let codec = Api.operationExecute.Input

    let permissive transform value =
        codec.Read "$" (transform value) |> Result.map codec.Write

    Expect.isFalse
        (refuses (permissive (stripMember memberName)) (mutations (extraMember memberName) seed))
        "A normalizer accepting an unknown member fails the property"

    Expect.isFalse
        (refuses (permissive (aliasKind kind "CASE")) (mutations (unknownKind kind) seed))
        "A normalizer accepting an unknown kind fails the property"

[<Tests>]
let tests =
    testList
        "closed boundary strictness"
        [
            testCase
                "every CLI verb refuses generated undeclared flags with a permissive negative control"
                cliStrictness
            testCase
                "every endpoint refuses unknown members and kinds with accepting negative controls"
                endpointStrictness
        ]
