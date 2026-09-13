namespace ClaimCore.Docs

open System
open System.IO
open System.Xml
open System.Xml.Linq

type internal TrxDefinition =
    {
        TestId: Guid
        ExecutionId: Guid
        Name: string
        CodeBase: string
    }

[<RequireQualifiedAccess>]
module internal TrxStructure =
    let ns = XNamespace.Get("http://microsoft.com/schemas/VisualStudio/TeamTest/2010")

    let attribute name (element: XElement) =
        match element.Attribute(XName.Get(name)) |> Option.ofObj with
        | Some value -> Ok value.Value
        | None -> Error $"Missing TRX attribute '{name}'."

    let private integerAttribute name element =
        match attribute name element with
        | Error message -> Error message
        | Ok value ->
            match Int32.TryParse(value) with
            | true, parsed when parsed >= 0 -> Ok parsed
            | _ -> Error $"TRX counter '{name}' is invalid."

    let private guidAttribute name element =
        attribute name element
        |> Result.bind (fun value ->
            match Guid.TryParse(value) with
            | true, parsed -> Ok parsed
            | _ -> Error $"TRX attribute '{name}' is not a UUID.")

    let read path =
        try
            let info = FileInfo(path)

            if info.Length > 16L * 1024L * 1024L then
                Error "TRX report exceeds its bounded size."
            else
                let settings =
                    XmlReaderSettings(DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null)

                use stream = File.OpenRead(path)
                use reader = XmlReader.Create(stream, settings)
                Ok(XDocument.Load(reader, LoadOptions.PreserveWhitespace))
        with error ->
            Error(error.GetType().Name + ": " + error.Message)

    let private counterNames =
        [
            "failed"
            "error"
            "timeout"
            "aborted"
            "inconclusive"
            "passedButRunAborted"
            "notRunnable"
            "notExecuted"
            "disconnected"
            "warning"
            "completed"
            "inProgress"
            "pending"
        ]

    let private totals expected counts =
        match
            integerAttribute "total" counts,
            integerAttribute "executed" counts,
            integerAttribute "passed" counts
        with
        | Ok total, Ok executed, Ok passed ->
            let nonzero =
                counterNames
                |> List.map (fun name -> name, integerAttribute name counts)
                |> List.tryPick (fun (name, value) ->
                    match value with
                    | Ok 0 -> None
                    | Ok count -> Some $"TRX counter '{name}' is {count}; expected zero."
                    | Error message -> Some message)

            match nonzero with
            | Some message -> Error message
            | None when total <> expected || executed <> total || passed <> total || total = 0 ->
                Error
                    $"TRX count mismatch: total={total}, executed={executed}, passed={passed}, expected={expected}."
            | None -> Ok(total, passed)
        | _ -> Error "TRX total/executed/passed counters are missing or invalid."

    let counters expected (root: XElement) =
        let summaries = root.Elements(ns + "ResultSummary") |> Seq.toList

        let elements =
            summaries
            |> List.collect (fun item -> item.Elements(ns + "Counters") |> Seq.toList)

        match summaries, elements with
        | [ summary ], [ counts ] ->
            let expectedAttributes = set ([ "total"; "executed"; "passed" ] @ counterNames)
            let actualAttributes = counts.Attributes() |> Seq.map _.Name.LocalName |> Set.ofSeq

            match attribute "outcome" summary with
            | Error _ -> Error "TRX summary outcome is missing."
            | Ok outcome when outcome <> "Completed" ->
                Error "TRX summary outcome is not Completed."
            | Ok _ when actualAttributes <> expectedAttributes ->
                Error "TRX counters have missing or unexpected attributes."
            | Ok _ -> totals expected counts
        | _ -> Error "TRX must contain exactly one summary and counter set."

    let definitions (root: XElement) =
        let parsed = ResizeArray<TrxDefinition>()
        let errors = ResizeArray<string>()

        for test in root.Descendants(ns + "UnitTest") do
            let methodNode = test.Element(ns + "TestMethod") |> Option.ofObj
            let executions = test.Elements(ns + "Execution") |> Seq.toList

            match attribute "id" test, attribute "name" test, methodNode, executions with
            | Ok rawId, Ok name, Some methodValue, [ executionNode ] ->
                match
                    Guid.TryParse(rawId),
                    guidAttribute "id" executionNode,
                    attribute "codeBase" methodValue
                with
                | (true, id), Ok executionId, Ok codeBase ->
                    parsed.Add(
                        {
                            TestId = id
                            ExecutionId = executionId
                            Name = name
                            CodeBase = codeBase
                        }
                    )
                | _ -> errors.Add("TRX test definition has invalid identity.")
            | _ -> errors.Add("TRX test definition is incomplete.")

        let ids = parsed |> Seq.map _.TestId |> Seq.toList
        let executionIds = parsed |> Seq.map _.ExecutionId |> Seq.toList

        if errors.Count > 0 then
            Error(String.concat " " errors)
        elif ids.Length <> (ids |> Set.ofList |> Set.count) then
            Error "TRX test definitions contain duplicate IDs."
        elif executionIds.Length <> (executionIds |> Set.ofList |> Set.count) then
            Error "TRX test definitions contain duplicate execution IDs."
        else
            Ok(List.ofSeq parsed)

    let entries (root: XElement) =
        let containers = root.Elements(ns + "TestEntries") |> Seq.toList

        match containers with
        | [ container ] ->
            let parsed =
                container.Elements(ns + "TestEntry")
                |> Seq.map (fun entry ->
                    match attribute "testId" entry, attribute "executionId" entry with
                    | Ok testId, Ok executionId ->
                        match Guid.TryParse(testId), Guid.TryParse(executionId) with
                        | (true, test), (true, execution) -> Ok(test, execution)
                        | _ -> Error "TRX test entry has invalid identity."
                    | _ -> Error "TRX test entry is incomplete.")
                |> Seq.toList

            if parsed |> List.exists Result.isError then
                Error "TRX test entries are missing or invalid."
            else
                let values = parsed |> List.choose Result.toOption

                if values.IsEmpty then
                    Error "TRX test entries are missing or invalid."
                elif values.Length <> (values |> Set.ofList |> Set.count) then
                    Error "TRX test entries contain duplicate identities."
                else
                    Ok(Set.ofList values)
        | _ -> Error "TRX must contain exactly one test-entry set."
