namespace ClaimCore.Cli

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.HostSecurity

/// Only public connection settings are members of the profile format.
module internal CliProfile =
    let maximumBytes = 32768

    let fields =
        [
            CliSetting.ServiceUrl, "serviceUrl"
            CliSetting.Issuer, "issuer"
            CliSetting.ClientId, "clientId"
            CliSetting.AuthMode, "authMode"
            CliSetting.ServiceTrustRoot, "serviceTrustRootFile"
            CliSetting.OidcTrustRoot, "oidcTrustRootFile"
        ]

    let private invalid () = Error ClientFailure.Configuration

    let location read =
        let root =
            match read "XDG_CONFIG_HOME" with
            | Some value when not (String.IsNullOrEmpty value) -> value
            | _ ->
                match read "HOME" with
                | Some home when not (String.IsNullOrEmpty home) -> Path.Combine(home, ".config")
                | _ -> ""

        if OperatingSystem.IsWindows() || not (Path.IsPathFullyQualified root) then
            invalid ()
        else
            Ok(Path.Combine(root, "claimcore", "profile.json"))

    let private field (name, value) =
        match fields |> List.tryFind (fun (_, memberName) -> memberName = name), value with
        | Some(setting, _), JsonValue.String value when value.Length <= 8192 -> Ok(setting, value)
        | _ -> invalid ()

    let private decode bytes =
        match StrictJson.read maximumBytes bytes with
        | Ok(JsonValue.Object members) ->
            members
            |> List.fold
                (fun state item ->
                    state
                    |> Result.bind (fun values ->
                        field item |> Result.map (fun item -> item :: values)))
                (Ok [])
            |> Result.map Map.ofList
        | _ -> invalid ()

    let read path =
        match PrivateFile.read maximumBytes path with
        | Error PrivateFileFailure.Missing ->
            if (FileInfo path).LinkTarget <> null then
                invalid ()
            else
                Ok Map.empty
        | Error _ -> invalid ()
        | Ok bytes ->
            try
                decode bytes
            finally
                CryptographicOperations.ZeroMemory(Span<byte> bytes)

    let private directoryMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    let private directory path =
        if Directory.Exists path then
            let info = DirectoryInfo path

            if info.LinkTarget <> null || File.GetUnixFileMode path <> directoryMode then
                invalid ()
            else
                Ok()
        elif File.Exists path then
            invalid ()
        else
            Directory.CreateDirectory(path, directoryMode) |> ignore

            if File.GetUnixFileMode path = directoryMode then
                Ok()
            else
                invalid ()

    let private encoded values =
        fields
        |> List.choose (fun (setting, name) ->
            values
            |> Map.tryFind setting
            |> Option.map (fun value -> name, JsonValue.String value))
        |> JsonValue.Object
        |> WireWriter.indentedBytes

    /// The caller validates public values first. No environment-derived value is written implicitly.
    let write path (values: Map<CliSetting, string>) =
        let targetDirectory = Path.GetDirectoryName(path: string) |> nonNull

        let temporary =
            Path.Combine(targetDirectory, ".profile-" + Guid.NewGuid().ToString("N"))

        try
            try
                let allowed = fields |> List.map fst |> Set.ofList

                let valid =
                    values
                    |> Map.forall (fun key value ->
                        Set.contains key allowed && value.Length <= 8192 && JsonValue.isText value)

                let bytes = encoded values

                (if valid && bytes.Length <= maximumBytes then
                     directory targetDirectory
                 else
                     invalid ())
                |> Result.bind (fun () ->
                    match read path with
                    | Error reason -> Error reason
                    | Ok _ ->
                        let options =
                            FileStreamOptions(
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.None,
                                UnixCreateMode = fileMode
                            )

                        do
                            use stream = new FileStream(temporary, options)
                            stream.Write bytes
                            stream.Flush(true)

                        File.Move(temporary, path, true)
                        Ok())
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? ArgumentException
            | :? NotSupportedException -> invalid ()
        finally
            if File.Exists temporary then
                File.Delete temporary
