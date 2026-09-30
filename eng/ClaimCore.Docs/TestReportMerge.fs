namespace ClaimCore.Docs

open System
open System.IO
open System.Text
open System.Xml
open System.Xml.Linq

/// Joins the reports of concurrent partitions of one test assembly into the single report its stage
/// registers. Every input must already be a complete, all-passing report on its own; the merged
/// report is then verified against the compiled inventory exactly as an unpartitioned run is, so a
/// missing, duplicated or renamed test in any partition still fails there.
[<RequireQualifiedAccess>]
module TestReportMerge =
    let private child (name: string) (element: XElement) =
        element.Element(TrxStructure.ns + name) |> Option.ofObj

    let private resultNames (root: XElement) =
        root.Descendants(TrxStructure.ns + "UnitTestResult")
        |> Seq.choose (fun result -> TrxStructure.attribute "testName" result |> Result.toOption)
        |> Seq.toList

    let private validateInput (assembly: string) (path: string) =
        TrxStructure.read path
        |> Result.bind (fun document ->
            match document.Root |> Option.ofObj with
            | None -> Error "TRX root is missing."
            | Some root ->
                let names = resultNames root

                Evidence.parseTrx
                    {
                        StageId = "partition"
                        Assembly = assembly
                        FileName = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue ""
                        ExpectedTests = names.Length
                        ExpectedNames = Set.ofList names
                    }
                    path
                |> Result.map (fun _ -> root))

    let private counters (root: XElement) =
        child "ResultSummary" root |> Option.bind (child "Counters")

    let private total (attribute: string) (roots: XElement list) =
        roots
        |> List.sumBy (fun root ->
            counters root
            |> Option.bind (fun value -> value.Attribute(XName.Get(attribute)) |> Option.ofObj)
            |> Option.map (fun value -> int value.Value)
            |> Option.defaultValue 0)

    let private append
        (name: string)
        (container: string)
        (roots: XElement list)
        (target: XElement)
        =
        match child container target with
        | None -> ()
        | Some parent ->
            for root in List.tail roots do
                match child container root with
                | Some source ->
                    for item in source.Elements(TrxStructure.ns + name) do
                        parent.Add(XElement(item))
                | None -> ()

    let private times (roots: XElement list) (target: XElement) =
        match child "Times" target with
        | None -> ()
        | Some element ->
            let read (attribute: string) (root: XElement) =
                child "Times" root
                |> Option.bind (fun value -> value.Attribute(XName.Get(attribute)) |> Option.ofObj)
                |> Option.bind (fun value ->
                    match DateTimeOffset.TryParse(value.Value) with
                    | true, parsed -> Some parsed
                    | _ -> None)

            let pick (attribute: string) (choose: DateTimeOffset list -> DateTimeOffset) =
                let values = roots |> List.choose (read attribute)

                if values.Length = roots.Length then
                    element.SetAttributeValue(XName.Get(attribute), (choose values).ToString("O"))

            pick "creation" List.min
            pick "queuing" List.min
            pick "start" List.min
            pick "finish" List.max

    let private merged (roots: XElement list) =
        let target = XElement(List.head roots)
        target.SetAttributeValue(XName.Get("id"), Guid.NewGuid().ToString())
        append "UnitTest" "TestDefinitions" roots target
        append "TestEntry" "TestEntries" roots target
        append "UnitTestResult" "Results" roots target
        times roots target

        counters target
        |> Option.iter (fun value ->
            for attribute in [ "total"; "executed"; "passed" ] do
                value.SetAttributeValue(XName.Get(attribute), total attribute roots))

        target

    let private write (destination: string) (root: XElement) =
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination) |> Option.ofObj |> Option.defaultValue "."
        )
        |> ignore

        use stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)

        use writer =
            XmlWriter.Create(
                stream,
                XmlWriterSettings(Encoding = UTF8Encoding(false), Indent = true)
            )

        XDocument(XDeclaration("1.0", "utf-8", null), [| box root |]).Save(writer)

    let private resolve (root: RepositoryRoot) (relative: string) =
        Repository.registeredPath root relative
        |> Result.bind (Repository.ensureExistingSafe root)

    let private allOk (results: Result<'value, string> list) =
        match
            results
            |> List.tryPick (function
                | Error message -> Some message
                | Ok _ -> None)
        with
        | Some message -> Error message
        | None -> Ok(results |> List.choose Result.toOption)

    /// Merge `inputs` (repository-relative TRX paths) into the new report `output`.
    let merge (root: RepositoryRoot) (assembly: string) (output: string) (inputs: string list) =
        match Repository.registeredPath root output, allOk (inputs |> List.map (resolve root)) with
        | Error message, _
        | _, Error message -> Error message
        | Ok destination, Ok _ when inputs.IsEmpty || File.Exists(destination) ->
            Error "Merging needs at least one input and a new output report."
        | Ok destination, Ok paths ->
            allOk (paths |> List.map (validateInput assembly))
            |> Result.bind (fun roots ->
                let names = roots |> List.collect resultNames

                if names.Length <> (Set.ofList names).Count then
                    Error "Partition reports overlap: a test appears in more than one."
                else
                    write destination (merged roots)
                    Ok names.Length)
