namespace ClaimCore.Docs

open System
open System.IO
open System.Xml.Linq

type EvidenceResult =
    {
        Stages: StageManifest list
        Reports: TestReport list
        Contracts: ContractEvidence list
        Reviews: ContractReview list
    }

[<RequireQualifiedAccess>]
module Evidence =
    let private execution
        (expectedAssembly: string)
        (definitions: TrxDefinition list)
        (element: XElement)
        =
        match
            TrxStructure.attribute "testId" element,
            TrxStructure.attribute "executionId" element,
            TrxStructure.attribute "testName" element,
            TrxStructure.attribute "outcome" element
        with
        | Ok rawId, Ok rawExecutionId, Ok name, Ok outcome ->
            match Guid.TryParse(rawId), Guid.TryParse(rawExecutionId) with
            | (true, id), (true, executionId) ->
                match definitions |> List.tryFind (fun candidate -> candidate.TestId = id) with
                | None -> Error "TRX result does not resolve to one definition."
                | Some definition ->
                    let assembly =
                        definition.CodeBase.Replace(char 92, '/')
                        |> fun value -> value.Split('/') |> Array.last
                        |> Path.GetFileNameWithoutExtension
                        |> Option.ofObj
                        |> Option.defaultValue ""

                    if definition.Name <> name then
                        Error "TRX result/definition names disagree."
                    elif definition.ExecutionId <> executionId then
                        Error "TRX result/definition execution IDs disagree."
                    elif assembly <> expectedAssembly then
                        Error "TRX codeBase names the wrong test assembly."
                    elif outcome <> "Passed" then
                        Error $"TRX result '{name}' is not Passed."
                    else
                        Ok(
                            (id, executionId),
                            {
                                Name = name
                                TestId = id
                                Outcome = outcome
                                Assembly = assembly
                            }
                        )
            | _ -> Error "TRX result identity is invalid."
        | _ -> Error "TRX result is missing identity or outcome."

    let private successful parsed =
        match
            parsed
            |> List.tryPick (function
                | Error value -> Some value
                | _ -> None)
        with
        | Some error -> Error error
        | None -> Ok(parsed |> List.choose Result.toOption)

    let private reconcile
        (expected: TestReportDefinition)
        (definitions: TrxDefinition list)
        (expectedEntries: Set<Guid * Guid>)
        (values: ((Guid * Guid) * TestExecution) list)
        =
        let resultIdentities = values |> List.map fst
        let resultIds = resultIdentities |> List.map fst
        let definitionIds = definitions |> List.map _.TestId

        if resultIds.Length <> (resultIds |> Set.ofList |> Set.count) then
            Error "TRX contains duplicate test results."
        elif Set.ofList resultIds <> Set.ofList definitionIds then
            Error "TRX definitions and results do not reconcile."
        elif Set.ofList resultIdentities <> expectedEntries then
            Error "TRX results and test entries do not reconcile."
        elif
            values |> List.map (fun (_, test) -> test.Name) |> Set.ofList
            <> expected.ExpectedNames
        then
            Error "TRX test names differ from the compiled identity inventory."
        else
            Ok(values |> List.map snd)

    let private executions
        (expected: TestReportDefinition)
        total
        definitions
        expectedEntries
        (root: XElement)
        =
        let results = root.Descendants(TrxStructure.ns + "UnitTestResult") |> Seq.toList

        if results.Length <> total then
            Error "TRX result count differs from its summary."
        else
            results
            |> List.map (execution expected.Assembly definitions)
            |> successful
            |> Result.bind (reconcile expected definitions expectedEntries)

    let private parsedReport expected (root: XElement) =
        match
            TrxStructure.attribute "id" root,
            TrxStructure.counters expected.ExpectedTests root,
            TrxStructure.definitions root,
            TrxStructure.entries root
        with
        | Ok rawRunId, Ok(total, passed), Ok declared, Ok testEntries ->
            match Guid.TryParse(rawRunId), executions expected total declared testEntries root with
            | (true, runId), Ok tests ->
                Ok
                    {
                        Assembly = expected.Assembly
                        RunId = runId
                        Total = total
                        Passed = passed
                        Tests = tests
                    }
            | (false, _), _ -> Error "TRX run ID is not a UUID."
            | _, Error message -> Error message
        | Error message, _, _, _
        | _, Error message, _, _
        | _, _, Error message, _
        | _, _, _, Error message -> Error message

    let parseTrx expected path =
        match TrxStructure.read path with
        | Error message -> Error message
        | Ok document ->
            match document.Root |> Option.ofObj with
            | None -> Error "TRX root is missing."
            | Some root when root.Name <> TrxStructure.ns + "TestRun" ->
                Error "TRX root or namespace is invalid."
            | Some root -> parsedReport expected root
