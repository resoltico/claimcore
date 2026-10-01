namespace ClaimCore.Docs

open System
open System.IO
open System.Text

[<NoEquality; NoComparison>]
type DocumentationAssessment =
    {
        Documents: MarkdownFile list
        Generated: GeneratedDocument list
        VirtualDocuments: MarkdownFile list
        Links: int
        Contracts: ContractDeclaration list
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

    let private completeAssessment (root: RepositoryRoot) documents generated =
        let virtualDocs = virtualDocuments documents generated

        match Links.check root virtualDocs, Contracts.declarations virtualDocs with
        | Error errors, _
        | _, Error errors -> Error errors
        | Ok links, Ok contracts ->
            match ContractTokens.verify root contracts with
            | Error errors -> Error errors
            | Ok() ->
                Ok
                    {
                        Documents = documents
                        Generated = generated
                        VirtualDocuments = virtualDocs
                        Links = links
                        Contracts = contracts
                    }

    let private assess (root: RepositoryRoot) (runner: IProcessRunner) =
        match MarkdownModel.readAll root runner with
        | Error errors -> Error errors
        | Ok documents ->
            let context = { Root = root; Processes = runner }

            match Generators.generate context documents with
            | Error errors -> Error errors
            | Ok generated -> completeAssessment root documents generated

    let check (root: RepositoryRoot) (runner: IProcessRunner) =
        match ArchitectureManifest.requireCurrent root with
        | Error message -> Error [ Diagnostic.create DiagnosticCode.InvalidManifest message ]
        | Ok() ->
            match assess root runner with
            | Error errors -> Error errors
            | Ok assessment ->
                let drift =
                    assessment.Generated
                    |> List.filter (fun item -> item.Original.Text <> item.ExpectedText)
                    |> List.map (fun item ->
                        Diagnostic.create
                            DiagnosticCode.GeneratedDrift
                            $"Generated blocks are stale in '{item.Original.RelativePath}'.")

                if drift.IsEmpty then Ok assessment else Error drift

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
        | Some error -> Error [ error ]
        | None -> check root runner

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
            | Error errors -> Error errors
            | Ok assessment -> writeAssessed root runner assessment
        with error ->
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        (error.GetType().Name + ": " + error.Message)
                ]
