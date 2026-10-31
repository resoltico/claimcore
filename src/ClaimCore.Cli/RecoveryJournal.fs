namespace ClaimCore.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.HostSecurity

type internal RecoveryScope =
    {
        ServiceOrigin: string
        InstallationId: Guid
        ActorId: Guid
    }

[<NoComparison>]
type internal RecoveryIdentity =
    {
        ServiceOrigin: string
        InstallationId: Guid
        ActorId: Guid
        AttemptId: Guid
        OperationId: Guid
        Subject: string
        Act: string
    }

    member this.Scope: RecoveryScope =
        {
            ServiceOrigin = this.ServiceOrigin
            InstallationId = this.InstallationId
            ActorId = this.ActorId
        }

/// Private identity-only files. Each local attempt publishes independently; unresolved files never expire.
module internal RecoveryJournal =
    let private directoryMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let private maximumBytes = 4096

    let identityCodec =
        (Codec.record (fun origin installation actor attempt operation subject act () ->
            {
                ServiceOrigin = origin
                InstallationId = installation
                ActorId = actor
                AttemptId = attempt
                OperationId = operation
                Subject = subject
                Act = act
            })
        : Fields<RecoveryIdentity, _>)
        |> Codec.field "serviceOrigin" _.ServiceOrigin (Codec.boundedText 1 2048)
        |> Codec.field "installationId" _.InstallationId Codec.uuid
        |> Codec.field "actorId" _.ActorId Codec.uuid
        |> Codec.field "attemptId" _.AttemptId Codec.uuid
        |> Codec.field "operationId" _.OperationId Codec.uuid
        |> Codec.field "subject" _.Subject (Codec.boundedText 1 40)
        |> Codec.field "act" _.Act (Codec.boundedText 1 40)
        |> Codec.field
            "format"
            (fun _ -> ())
            (Codec.integer 1L 1L |> Codec.map (fun _ -> ()) (fun () -> 1L))
        |> Codec.build


    let private valid (record: RecoveryIdentity) =
        let mutable origin = Unchecked.defaultof<Uri>

        Uri.TryCreate(record.ServiceOrigin + "/", UriKind.Absolute, &origin)
        && origin.Scheme = Uri.UriSchemeHttps
        && origin.GetLeftPart(UriPartial.Authority) = record.ServiceOrigin
        && String.IsNullOrEmpty origin.UserInfo
        && origin.AbsolutePath = "/"
        && [ record.InstallationId; record.ActorId; record.AttemptId; record.OperationId ]
           |> List.forall ((<>) Guid.Empty)
        && [ record.Subject; record.Act ]
           |> List.forall (fun value ->
               value.Length > 0
               && value.Length <= 40
               && value |> Seq.forall (fun c -> c = '_' || c >= 'A' && c <= 'Z'))

    let location read =
        let root =
            match read "XDG_STATE_HOME" with
            | Some value when not (String.IsNullOrEmpty value) -> value
            | _ ->
                read "HOME"
                |> Option.map (fun home -> Path.Combine(home, ".local", "state"))
                |> Option.defaultValue ""

        if OperatingSystem.IsWindows() || not (Path.IsPathFullyQualified root) then
            Error()
        else
            Ok(Path.Combine(root, "claimcore"))

    let private originKey origin =
        Encoding.UTF8.GetBytes(origin: string)
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let private prefix (record: RecoveryScope) =
        originKey record.ServiceOrigin
        + "-"
        + record.InstallationId.ToString("D")
        + "-"
        + record.ActorId.ToString("D")
        + "-"

    let private path directory (record: RecoveryIdentity) =
        Path.Combine(directory, prefix record.Scope + record.AttemptId.ToString("D") + ".json")

    let private privateDirectory directory =
        if Directory.Exists directory then
            let info = DirectoryInfo directory
            info.LinkTarget = null && File.GetUnixFileMode directory = directoryMode
        elif File.Exists directory then
            false
        else
            Directory.CreateDirectory(directory, directoryMode) |> ignore
            File.GetUnixFileMode directory = directoryMode

    let private readRecord file =
        if (FileInfo file).LinkTarget <> null || File.GetUnixFileMode file <> fileMode then
            Error()
        else
            match PrivateFile.read maximumBytes file with
            | Error _ -> Error()
            | Ok bytes ->
                try
                    Codec.decode maximumBytes identityCodec bytes
                    |> Result.mapError (fun _ -> ())
                    |> Result.bind (fun record -> if valid record then Ok record else Error())
                finally
                    CryptographicOperations.ZeroMemory(Span<byte> bytes)

    let save directory record =
        let temporary = Path.Combine(directory, ".attempt-" + Guid.NewGuid().ToString("N"))

        try
            try
                if not (valid record) || not (privateDirectory directory) then
                    Error()
                else
                    let destination = path directory record
                    let bytes = Codec.encode identityCodec record

                    use stream =
                        new FileStream(
                            temporary,
                            FileStreamOptions(
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.None,
                                UnixCreateMode = fileMode
                            )
                        )

                    stream.Write bytes
                    stream.Flush(true)
                    stream.Dispose()

                    if File.Exists destination then
                        if readRecord destination = Ok record then Ok() else Error()
                    else
                        File.Move(temporary, destination, false)
                        Ok()
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? ArgumentException
            | :? NotSupportedException -> Error()
        finally
            if File.Exists temporary then
                try
                    File.Delete temporary
                with _ ->
                    ()

    let remove directory record =
        try
            if not (privateDirectory directory) then
                Error()
            else
                let target = path directory record

                if not (File.Exists target) then
                    Ok()
                elif readRecord target = Ok record then
                    File.Delete target
                    Ok()
                else
                    Error()
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error()

    let list directory scope =
        try
            if not (Directory.Exists directory) then
                Ok []
            elif not (privateDirectory directory) then
                Error()
            else
                Directory.EnumerateFiles(directory, prefix scope + "*.json")
                |> Seq.fold
                    (fun result file ->
                        result
                        |> Result.bind (fun records ->
                            readRecord file
                            |> Result.bind (fun record ->
                                if
                                    record.ServiceOrigin = scope.ServiceOrigin
                                    && record.InstallationId = scope.InstallationId
                                    && record.ActorId = scope.ActorId
                                    && path directory record = file
                                then
                                    Ok(record :: records)
                                else
                                    Error())))
                    (Ok [])
                |> Result.map (List.sortBy _.AttemptId)
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error()
