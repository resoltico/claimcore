namespace ClaimCore.Docs

open System
open System.IO
open System.Text

[<NoEquality; NoComparison>]
type DocumentationAssessment =
    {
        Source: SourceIdentity
        Documents: MarkdownFile list
        Generated: GeneratedDocument list
        VirtualDocuments: MarkdownFile list
        Links: int
        Contracts: ContractDeclaration list
        Reviews: ContractReview list
    }

[<RequireQualifiedAccess>]
module DocumentationCommands =
    let private virtualDocuments documents generated =
        let replacements =
            generated
            |> List.map (fun item -> item.Original.FullPath, item.ExpectedText)
            |> Map.ofList

        documents
        |> List.map (fun document ->
            match replacements |> Map.tryFind document.FullPath with
            | None -> document
            | Some text -> MarkdownModel.fromText document.RelativePath document.FullPath text)

    let private completeAssessment root runner source documents generated =
        let virtualDocs = virtualDocuments documents generated

        match Links.check root virtualDocs, Contracts.declarations virtualDocs with
        | Error errors, _
        | _, Error errors -> Error errors
        | Ok links, Ok contracts ->
            match Reviews.verify root contracts (DateOnly.FromDateTime(DateTime.UtcNow)) with
            | Error errors -> Error errors
            | Ok reviews ->
                match Provenance.sourceIdentity root runner with
                | Error message -> Error [ Diagnostic.create DiagnosticCode.Invocation message ]
                | Ok after when after.ContentSha256 <> source.ContentSha256 ->
                    Error
                        [
                            Diagnostic.create
                                DiagnosticCode.ConcurrentEdit
                                "Repository inputs changed during documentation assessment."
                        ]
                | Ok _ ->
                    Ok
                        {
                            Source = source
                            Documents = documents
                            Generated = generated
                            VirtualDocuments = virtualDocs
                            Links = links
                            Contracts = contracts
                            Reviews = reviews
                        }

    let private assess (root: RepositoryRoot) (runner: IProcessRunner) =
        match Provenance.sourceIdentity root runner, MarkdownModel.readAll root with
        | Error message, _ -> Error [ Diagnostic.create DiagnosticCode.Invocation message ]
        | _, Error errors -> Error errors
        | Ok source, Ok documents ->
            let context = { Root = root; Processes = runner }

            match Generators.generate context documents with
            | Error errors -> Error errors
            | Ok generated -> completeAssessment root runner source documents generated

    let private report
        (root: RepositoryRoot)
        (operation: string)
        (outcome: string)
        (assessment: DocumentationAssessment option)
        (errors: Diagnostic list)
        =
        let source, blocks, documents, links, contracts =
            match assessment with
            | Some value ->
                value.Source,
                value.Generated |> List.collect _.Blocks,
                value.Documents.Length,
                value.Links,
                value.Contracts |> List.map _.Id
            | None ->
                {
                    GitRevision = None
                    State = "unknown"
                    ContentSha256 = String.replicate 64 "0"
                    LocksSha256 = String.replicate 64 "0"
                },
                [],
                0,
                0,
                []

        DocumentationReport.write
            root
            operation
            outcome
            source
            blocks
            documents
            links
            contracts
            errors

    let internal includeReportWrite operation operationResult reportResult =
        match reportResult, operationResult with
        | Ok(), result -> result
        | Error message, Ok _ ->
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        $"Documentation '{operation}' report could not be written: {message}"
                ]
        | Error message, Error errors ->
            Error(
                errors
                @ [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        $"Documentation '{operation}' report could not be written: {message}"
                ]
            )

    let private finish root operation outcome assessment errors result =
        report root operation outcome assessment errors
        |> includeReportWrite operation result

    let check (root: RepositoryRoot) (runner: IProcessRunner) =
        match ArchitectureManifest.requireCurrent root with
        | Error message ->
            let errors = [ Diagnostic.create DiagnosticCode.InvalidManifest message ]
            finish root "check" "failed" None errors (Error errors)
        | Ok() ->

            match assess root runner with
            | Error errors -> finish root "check" "failed" None errors (Error errors)
            | Ok assessment ->
                let drift =
                    assessment.Generated
                    |> List.filter (fun item -> item.Original.Text <> item.ExpectedText)
                    |> List.map (fun item ->
                        Diagnostic.create
                            DiagnosticCode.GeneratedDrift
                            $"Generated blocks are stale in '{item.Original.RelativePath}'.")

                if drift.IsEmpty then
                    finish root "check" "passed" (Some assessment) [] (Ok assessment)
                else
                    finish root "check" "failed" (Some assessment) drift (Error drift)

    let private replaceOne (root: RepositoryRoot) (document: MarkdownFile) (expected: string) =
        let temporary =
            document.FullPath + ".claimcore-docs-" + Guid.NewGuid().ToString("N") + ".tmp"

        try
            try
                if Repository.ensureExistingSafe root document.FullPath |> Result.isError then
                    Error "The target or one of its parents became unsafe before replacement."
                elif Repository.sha256File document.FullPath <> document.Sha256 then
                    Error "The target changed before replacement."
                else
                    use stream =
                        new FileStream(
                            temporary,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None
                        )

                    let bytes = UTF8Encoding(false).GetBytes(expected)
                    stream.Write(bytes, 0, bytes.Length)
                    stream.Flush(true)
                    stream.Close()

                    if not (OperatingSystem.IsWindows()) then
                        File.SetUnixFileMode(temporary, File.GetUnixFileMode(document.FullPath))

                    if Repository.sha256File document.FullPath <> document.Sha256 then
                        Error "The target changed while its replacement was prepared."
                    else
                        File.Replace(temporary, document.FullPath, null, true)
                        Ok()
            with error ->
                Error(error.GetType().Name + ": " + error.Message)
        finally
            if File.Exists(temporary) then
                File.Delete(temporary)

    let private applyChanges root assessment =
        let changed =
            assessment.Generated
            |> List.filter (fun item -> item.Original.Text <> item.ExpectedText)

        let written = ResizeArray<string>()
        let mutable failure = None

        for item in changed do
            if failure.IsNone then
                match replaceOne root item.Original item.ExpectedText with
                | Ok() -> written.Add(item.Original.RelativePath)
                | Error message ->
                    let completed =
                        if written.Count = 0 then
                            "none"
                        else
                            String.concat ", " written

                    failure <-
                        Some(
                            Diagnostic.create
                                DiagnosticCode.ConcurrentEdit
                                $"Write failed after replacing [{completed}]: {message}"
                        )

        failure

    let private writeAssessed root runner assessment =
        match applyChanges root assessment with
        | Some error -> finish root "write" "failed" (Some assessment) [ error ] (Error [ error ])
        | None ->
            match check root runner with
            | Ok verified -> finish root "write" "passed" (Some verified) [] (Ok verified)
            | Error errors -> finish root "write" "failed" None errors (Error errors)

    let write (root: RepositoryRoot) (runner: IProcessRunner) =
        let lockPath = Path.Combine(root.Path, "artifacts/docs/write.lock")

        let directory =
            Path.GetDirectoryName(lockPath) |> Option.ofObj |> Option.defaultValue root.Path

        Directory.CreateDirectory(directory) |> ignore

        try
            use _writeLock =
                new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                )

            match assess root runner with
            | Error errors -> finish root "write" "failed" None errors (Error errors)
            | Ok assessment -> writeAssessed root runner assessment
        with error ->
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        (error.GetType().Name + ": " + error.Message)
                ]
