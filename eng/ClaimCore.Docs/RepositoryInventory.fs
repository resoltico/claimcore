namespace ClaimCore.Docs

open System.IO

[<RequireQualifiedAccess>]
module RepositoryInventory =
    let private validate root files =
        let duplicates =
            files
            |> List.groupBy (Repository.relativePath root)
            |> List.filter (fun (_, paths) -> paths.Length > 1)

        let collisions =
            files
            |> List.groupBy (Repository.relativePath root >> _.ToUpperInvariant())
            |> List.filter (fun (_, paths) -> paths.Length > 1)

        if not duplicates.IsEmpty then
            Error "Source inventory contains a duplicate path."
        elif not collisions.IsEmpty then
            Error "Source inventory contains a cross-platform path-case collision."
        else
            Ok(
                files
                |> List.sortWith (fun left right ->
                    System.StringComparer.Ordinal.Compare(
                        Repository.relativePath root left,
                        Repository.relativePath root right
                    ))
            )

    let sourceFiles (root: RepositoryRoot) =
        let rec walk directory =
            seq {
                for entry in Directory.EnumerateFileSystemEntries(directory) |> Seq.sort do
                    let relative = Repository.relativePath root entry

                    if RepositoryPathPolicy.excludedDirectory relative then
                        ()
                    elif Repository.isReparse entry then
                        raise (IOException($"Source inventory path is a symbolic link: {relative}"))
                    elif Directory.Exists(entry) then
                        yield! walk entry
                    elif not (File.Exists(entry)) then
                        raise (
                            IOException($"Source inventory path is not a regular file: {relative}")
                        )
                    elif not (RepositoryPathPolicy.excludedFile relative) then
                        yield entry
            }

        let files = walk root.Path |> Seq.toList

        match validate root files with
        | Ok validated -> validated
        | Error message -> raise (IOException(message))

    let gitSourceFiles (root: RepositoryRoot) (raw: string) =
        if raw.Length > 0 && raw[raw.Length - 1] <> char 0 then
            Error "Git source inventory is not NUL-terminated."
        else
            let fields = raw.Split(char 0)

            let relativePaths =
                if fields.Length = 0 then
                    []
                else
                    fields |> Array.take (fields.Length - 1) |> Array.toList

            if relativePaths |> List.exists System.String.IsNullOrEmpty then
                Error "Git source inventory contains an empty path."
            else
                let resolved =
                    relativePaths
                    |> List.map (fun relative ->
                        Repository.registeredPath root relative
                        |> Result.bind (Repository.ensureExistingSafe root)
                        |> Result.bind (fun path ->
                            if File.Exists(path) && not (Directory.Exists(path)) then
                                Ok path
                            else
                                Error $"Git source path '{relative}' is not a regular file."))

                match
                    resolved
                    |> List.tryPick (function
                        | Error message -> Some message
                        | Ok _ -> None)
                with
                | Some message -> Error message
                | None -> resolved |> List.choose Result.toOption |> validate root

    let lockFiles (root: RepositoryRoot) files =
        files
        |> List.filter (fun path ->
            let relative = Repository.relativePath root path

            Path.GetFileName(path) = "packages.lock.json"
            || relative = "web/package-lock.json"
            || relative = ".config/dotnet-tools.json")

    let aggregateHash (root: RepositoryRoot) paths =
        paths
        |> Seq.map (fun path ->
            let relative = Repository.relativePath root path

            "file"
            + string (char 0)
            + relative
            + string (char 0)
            + FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + string (char 0)
            + Repository.sha256File path
            + "\n")
        |> String.concat ""
        |> Repository.sha256Text
