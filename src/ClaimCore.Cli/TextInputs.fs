namespace ClaimCore.Cli

open System
open System.Diagnostics
open System.IO
open System.Text
open ClaimCore.Domain
open ClaimCore.Protocol

/// Text is acquired before operation preparation. The resulting LF text is reviewed/frozen exactly.
module internal TextInputs =
    let private utf8 = UTF8Encoding(false, true)

    let private bounded maximum (stream: Stream) =
        let bytes = Array.zeroCreate<byte>(maximum + 1)
        let mutable length = 0
        let mutable reading = true

        while reading && length < bytes.Length do
            let read = stream.Read(bytes, length, bytes.Length - length)

            if read = 0 then
                reading <- false
            else
                length <- length + read

        if length > maximum then
            raise (IOException "Text input exceeds its byte limit.")

        utf8.GetString(bytes, 0, length)

    let private file maximum path =
        let path = Path.GetFullPath path
        let info = FileInfo path

        if not info.Exists || info.Length > int64 maximum || info.Length = 0L then
            raise (IOException "A bounded nonempty regular text file is required.")

        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)

        if not stream.CanSeek then
            raise (IOException "A regular text file is required.")

        bounded maximum stream

    let private editor maximum =
        if OperatingSystem.IsWindows() then
            raise (IOException "Private editor files require a supported native platform.")

        let command =
            [ "VISUAL"; "EDITOR" ]
            |> List.tryPick (
                Environment.GetEnvironmentVariable
                >> Option.ofObj
                >> Option.filter (String.IsNullOrWhiteSpace >> not)
            )
            |> Option.defaultWith (fun () ->
                raise (IOException "Set VISUAL or EDITOR to an editor executable."))

        let directory =
            Path.Combine(Path.GetTempPath(), "claimcore-text-" + Guid.NewGuid().ToString("N"))

        let path = Path.Combine(directory, "entry.txt")

        let cleanup () =
            try
                if File.Exists path then
                    File.Delete path

                if Directory.Exists directory then
                    Directory.Delete directory
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ()

        let onExit = EventHandler(fun _ _ -> cleanup ())
        AppDomain.CurrentDomain.ProcessExit.AddHandler onExit

        try
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            )
            |> ignore

            use initial =
                new FileStream(
                    path,
                    FileStreamOptions(
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    )
                )

            initial.Dispose()
            // The configured value is one executable, never a shell expression or business text.
            let start = ProcessStartInfo(command, UseShellExecute = false)
            start.ArgumentList.Add path

            use editorProcess =
                Process.Start start
                |> Option.ofObj
                |> Option.defaultWith (fun () -> raise (IOException "Editor did not start."))

            editorProcess.WaitForExit()

            if editorProcess.ExitCode <> 0 then
                raise (IOException "Editor did not complete successfully.")

            file maximum path
        finally
            cleanup ()
            AppDomain.CurrentDomain.ProcessExit.RemoveHandler onExit

    let private source name (argument: string) =
        if argument = name then
            Some None
        elif argument.StartsWith(name + "=", StringComparison.Ordinal) then
            Some(Some(argument.Substring(name.Length + 1)))
        else
            None

    let expand interactive arguments =
        let operation =
            OperationCatalog.all
            |> List.tryFind (fun operation ->
                let words = CommandGrammar.words operation
                words = List.truncate words.Length arguments)

        match operation with
        | None -> Ok arguments
        | Some operation ->
            let inputs = CommandGrammar.plainTextInputs operation

            let rec collect completed remaining =
                match remaining with
                | [] -> Ok(List.rev completed)
                | argument :: rest ->
                    let selected =
                        inputs
                        |> List.tryPick (fun (input, constraints) ->
                            let flag =
                                "--" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)

                            [ "-file"; "-stdin"; "-editor" ]
                            |> List.tryPick (fun suffix ->
                                source (flag + suffix) argument
                                |> Option.map (fun value -> flag, suffix, value, constraints)))

                    match selected with
                    | None -> collect (argument :: completed) rest
                    | Some(flag, suffix, value, constraints) ->
                        let operand, tail =
                            if suffix = "-file" && value.IsNone then
                                match rest with
                                | value :: tail when
                                    not (value.StartsWith("--", StringComparison.Ordinal))
                                    ->
                                    Some value, tail
                                | _ -> None, rest
                            else
                                value, rest

                        if suffix = "-file" && operand.IsNone then
                            Error [ UsageError.MissingValue(flag + suffix) ]
                        elif suffix <> "-file" && value.IsSome then
                            Error [ UsageError.InvalidValue(flag + suffix, value.Value) ]
                        elif
                            (List.rev completed @ tail)
                            |> List.exists (fun argument ->
                                source flag argument |> Option.isSome
                                || [ "-file"; "-stdin"; "-editor" ]
                                   |> List.exists (fun suffix ->
                                       source (flag + suffix) argument |> Option.isSome))
                        then
                            Error [ UsageError.DuplicateOption flag ]
                        elif suffix = "-editor" && not interactive then
                            Error
                                [
                                    UsageError.NotApplicable(
                                        flag + suffix,
                                        "a noninteractive terminal"
                                    )
                                ]
                        elif suffix = "-stdin" && interactive then
                            Error
                                [
                                    UsageError.NotApplicable(
                                        flag + suffix,
                                        "interactive input; use a file or editor"
                                    )
                                ]
                        else
                            try
                                let maximum = constraints.MaximumUtf8Bytes * 2

                                let authored =
                                    match suffix with
                                    | "-file" -> file maximum operand.Value
                                    | "-editor" -> editor maximum
                                    | _ -> bounded maximum (Console.OpenStandardInput())

                                let prepared = authored.Replace("\r\n", "\n")

                                if utf8.GetByteCount prepared > constraints.MaximumUtf8Bytes then
                                    raise (
                                        IOException
                                            "Normalized text exceeds its permanent byte limit."
                                    )

                                collect (prepared :: flag :: completed) tail
                            with
                            | :? IOException
                            | :? UnauthorizedAccessException
                            | :? ArgumentException
                            | :? NotSupportedException
                            | :? System.ComponentModel.Win32Exception ->
                                Error
                                    [
                                        UsageError.InvalidValue(
                                            flag + suffix,
                                            "bounded UTF-8 text input unavailable"
                                        )
                                    ]

            collect [] arguments
