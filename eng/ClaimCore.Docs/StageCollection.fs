namespace ClaimCore.Docs

open System
open System.IO
open System.Collections.Generic

/// Downloaded artifact directories retain ownership until every record is checked.
[<RequireQualifiedAccess>]
module StageCollection =
    let private inputs = "artifacts/evidence-producers"

    let private safeFiles (root: RepositoryRoot) =
        Repository.registeredPath root inputs
        |> Result.bind (Repository.ensureExistingSafe root)
        |> Result.bind (fun directory ->
            if not (Directory.Exists directory) then
                Error "Producer evidence directory is missing."
            else
                let files = ResizeArray<string>()
                let mutable failure = None

                for entry in Directory.EnumerateFileSystemEntries directory do
                    match Repository.ensureExistingSafe root entry with
                    | Error _ -> failure <- Some "Unsafe producer artifact directory."
                    | Ok safe when not (Directory.Exists safe) ->
                        failure <- Some "Producer evidence must retain its artifact directory."
                    | Ok safe ->
                        for path in Directory.EnumerateFileSystemEntries safe do
                            match Repository.ensureExistingSafe root path with
                            | Ok file when File.Exists file && FileInfo(file).Length <= 8388608L ->
                                files.Add file
                            | _ -> failure <- Some "Unsafe or oversized producer evidence entry."

                match failure with
                | Some message -> Error message
                | None -> Ok(List.ofSeq files))

    let private read root runId attempt path =
        let bytes = File.ReadAllBytes path

        StageManifestFormat.parse bytes
        |> Result.mapError (fun _ -> "Producer manifest is malformed.")
        |> Result.bind (fun manifest ->
            match Stages.tryFind manifest.StageId with
            | None -> Error "Unregistered producer stage."
            | Some definition when definition.Producer = "evidence" ->
                Error "Downloaded artifacts cannot supply reconciliation-owned stages."
            | Some definition ->
                let artifact = $"claimcore-stage-{definition.Producer}-{runId}-{attempt}"

                let actual =
                    Directory.GetParent(path)
                    |> Option.ofObj
                    |> Option.map _.Name
                    |> Option.defaultValue ""

                if actual <> artifact || Path.GetFileName(path) <> definition.Id + ".json" then
                    Error
                        $"Stage '{definition.Id}' was supplied by the wrong producer or filename."
                elif manifest.RunId <> runId || manifest.Attempt <> attempt then
                    Error "Producer evidence has a stale run or attempt identity."
                else
                    let relative =
                        EvidenceReconciliation.stageManifestPath runId attempt definition.Id

                    StageManifestFormat.validate definition runId attempt manifest
                    |> Result.bind (fun () -> Repository.registeredPath root relative)
                    |> Result.bind (fun destination ->
                        if File.Exists destination || Directory.Exists destination then
                            Error "A producer manifest destination already exists."
                        else
                            Ok(definition.Id, relative, bytes)))

    let collect (root: RepositoryRoot) runId attempt =
        try
            safeFiles root
            |> Result.bind (fun paths ->
                let seen = HashSet<string>(StringComparer.Ordinal)
                let records = ResizeArray<string * byte array>()
                let mutable failure = None

                for path in paths do
                    match read root runId attempt path with
                    | Error message -> failure <- Some message
                    | Ok(id, relative, bytes) ->
                        if not (seen.Add id) then
                            failure <- Some $"Duplicate producer evidence for stage '{id}'."
                        else
                            records.Add(relative, bytes)

                if records.Count = 0 && failure.IsNone then
                    failure <- Some "No producer evidence was downloaded."

                match failure with
                | Some message -> Error message
                | None ->
                    // Validate the whole collection before writing even its first record.
                    let mutable result = Ok()

                    for relative, bytes in records do
                        result <-
                            result
                            |> Result.bind (fun () ->
                                AtomicFile.writeNew root relative bytes |> Result.map ignore)

                    result)
        with _ ->
            Error "Producer evidence could not be collected safely."
