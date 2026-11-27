namespace ClaimCore.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
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
        Task: string
        Refused: bool
    }

    member this.Scope: RecoveryScope =
        {
            ServiceOrigin = this.ServiceOrigin
            InstallationId = this.InstallationId
            ActorId = this.ActorId
        }

[<NoComparison>]
type internal RecoveryListing =
    {
        Records: RecoveryIdentity list
        Damaged: int
    }

module private RecoveryFileSync =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openDirectory(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int fsync(int descriptor)

    [<DllImport("libc")>]
    extern int close(int descriptor)

    let directory path =
        let descriptor = openDirectory (path, 0)

        if descriptor < 0 then
            raise (IOException("Cannot open the recovery directory for durable publication."))

        try
            if fsync descriptor <> 0 then
                raise (IOException("Cannot sync the recovery directory."))
        finally
            close descriptor |> ignore

/// Private identity and delivery knowledge. Each attempt publishes independently; unresolved files never expire.
module internal RecoveryJournal =
    let private directoryMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let private maximumBytes = 4096

    let identityCodec =
        (Codec.record (fun origin installation actor attempt operation task refused () ->
            {
                ServiceOrigin = origin
                InstallationId = installation
                ActorId = actor
                AttemptId = attempt
                OperationId = operation
                Task = task
                Refused = refused
            })
        : Fields<RecoveryIdentity, _>)
        |> Codec.field "serviceOrigin" _.ServiceOrigin (Codec.boundedText 1 2048)
        |> Codec.field "installationId" _.InstallationId Codec.uuid
        |> Codec.field "actorId" _.ActorId Codec.uuid
        |> Codec.field "attemptId" _.AttemptId Codec.uuid
        |> Codec.field "operationId" _.OperationId Codec.uuid
        |> Codec.field "task" _.Task (Codec.boundedText 1 40)
        |> Codec.field "refused" _.Refused Codec.boolean
        |> Codec.field
            "format"
            (fun _ -> ())
            (Codec.integer 4L 4L |> Codec.map (fun _ -> ()) (fun () -> 4L))
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
        && Regex.IsMatch(
            record.Task,
            "^[A-Za-z][A-Za-z0-9.]{0,39}\\z",
            RegexOptions.CultureInvariant
        )



    let location read =
        let configured setting =
            read (RecoveryRootSetting.token setting)
            |> Option.filter (String.IsNullOrEmpty >> not)
            |> Option.map (fun path -> setting, path)

        let root =
            configured RecoveryRootSetting.XdgStateHome
            |> Option.orElseWith (fun () ->
                configured RecoveryRootSetting.Home
                |> Option.map (fun (setting, home) ->
                    setting, Path.Combine(home, ".local", "state")))

        match root with
        | _ when OperatingSystem.IsWindows() -> Error ClientFailure.RecoveryUnavailable
        | None -> Error ClientFailure.RecoveryUnavailable
        | Some(setting, path) when not (Path.IsPathFullyQualified path) ->
            Error(ClientFailure.RecoveryRootNotAbsolute setting)
        | Some(_, path) -> Ok(Path.Combine(path, "claimcore"))

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

    let private attemptPath directory scope (attemptId: Guid) =
        Path.Combine(directory, prefix scope + attemptId.ToString("D") + ".json")

    let private path directory (record: RecoveryIdentity) =
        attemptPath directory record.Scope record.AttemptId

    let private privateDirectory directory =
        let rec create (path: string) =
            if not (Directory.Exists path) then
                let parent = (Directory.GetParent(path) |> nonNull).FullName
                create parent
                Directory.CreateDirectory(path, directoryMode) |> ignore
                RecoveryFileSync.directory parent

        if File.Exists directory then
            false
        else
            create directory
            let info = DirectoryInfo directory
            info.LinkTarget = null && File.GetUnixFileMode directory = directoryMode

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

                    let mutable published = false

                    try
                        File.Move(temporary, destination, false)
                        published <- true
                    with :? IOException when File.Exists destination ->
                        match readRecord destination with
                        | Ok current when current = record -> published <- true
                        | Ok current when
                            { current with
                                Refused = record.Refused
                            }
                                =
                                record
                            ->
                            File.Move(temporary, destination, true)
                            published <- true
                        | _ -> ()

                    if published then
                        RecoveryFileSync.directory directory
                        Ok()
                    else
                        Error()
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

    /// Delivery knowledge changes only the owned attempt's status, never its identity.
    let markRefused directory record =
        save directory { record with Refused = true }

    let remove directory record =
        try
            if not (privateDirectory directory) then
                Error()
            else
                let target = path directory record

                if not (File.Exists target) then
                    Ok()
                elif
                    readRecord target
                    |> Result.map (fun current ->
                        { current with
                            Refused = record.Refused
                        })
                        =
                        Ok record
                then
                    File.Delete target
                    Ok()
                else
                    Error()
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error()

    let private readOwned directory scope file =
        readRecord file
        |> Result.bind (fun record ->
            if record.Scope = scope && path directory record = file then
                Ok record
            else
                Error())

    let find directory scope attempt =
        try
            let target = attemptPath directory scope attempt

            if not (File.Exists target) && isNull ((FileInfo target).LinkTarget) then
                Ok None
            elif not (privateDirectory directory) then
                Error()
            else
                readOwned directory scope target |> Result.map Some
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error()

    let list directory scope =
        let empty = { Records = []; Damaged = 0 }

        try
            if not (Directory.Exists directory) then
                Ok empty
            elif not (privateDirectory directory) then
                Error()
            else
                Directory.EnumerateFiles(directory, prefix scope + "*.json")
                |> Seq.fold
                    (fun listing file ->
                        let record =
                            try
                                readOwned directory scope file
                            with
                            | :? IOException
                            | :? UnauthorizedAccessException -> Error()

                        match record with
                        | Ok record ->
                            { listing with
                                Records = record :: listing.Records
                            }
                        | Error() ->
                            { listing with
                                Damaged = listing.Damaged + 1
                            })
                    empty
                |> fun listing ->
                    Ok
                        { listing with
                            Records = List.sortBy _.AttemptId listing.Records
                        }
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error()
