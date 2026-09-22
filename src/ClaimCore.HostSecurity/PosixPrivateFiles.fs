namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Security.Cryptography
open Microsoft.Win32.SafeHandles

module internal PosixPrivateFiles =
    let private pathParts (path: string) =
        if
            String.IsNullOrEmpty(path)
            || path.IndexOf('\u0000') >= 0
            || not (Path.IsPathFullyQualified(path))
            || not (path.StartsWith("/", StringComparison.Ordinal))
            || path.EndsWith("/", StringComparison.Ordinal)
        then
            raise (ArgumentException("Private-file path must be absolute."))

        let parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

        if
            parts.Length = 0
            || parts |> Array.exists (fun part -> part = "." || part = "..")
            || path <> "/" + String.Join("/", parts)
        then
            raise (ArgumentException("Private-file path is not canonical."))

        parts

    let withParent (path: string) (action: SafeFileHandle -> string -> 'a) : 'a =
        let parts = pathParts path
        let native = PosixPrivateNative.flags ()
        let directoryFlags = native.Directory ||| native.NoFollow ||| native.CloseOnExec

        let rec walk (parent: SafeFileHandle) index =
            PosixPrivateNative.safeDirectory (parent.DangerousGetHandle())

            if index = parts.Length - 1 then
                action parent parts[index]
            else
                use next =
                    PosixPrivateNative.openHandle
                        (int (parent.DangerousGetHandle()))
                        parts[index]
                        directoryFlags

                walk next (index + 1)

        use root = PosixPrivateNative.openHandle -1 "/" directoryFlags
        walk root 0

    let private current (parent: SafeFileHandle) name opened =
        PosixPrivateNative.statEntry (int (parent.DangerousGetHandle())) name
        |> Option.exists (fun final ->
            PosixPrivateNative.sameFile opened final
            && PosixPrivateNative.ownerPrivateRegular final)

    let private removeCreated (parent: SafeFileHandle) name opened directory =
        let descriptor = int (parent.DangerousGetHandle())

        match PosixPrivateNative.statEntry descriptor name with
        | Some final when PosixPrivateNative.sameFile opened final ->
            try
                PosixPrivateNative.unlink descriptor name directory
                PosixPrivateNative.syncDirectory descriptor
            with :? IOException ->
                ()
        | _ -> ()

    let read maximum path =
        withParent path (fun parent name ->
            let native = PosixPrivateNative.flags ()
            let openFlags = native.NoFollow ||| native.CloseOnExec ||| native.NonBlock

            use handle =
                PosixPrivateNative.openHandle (int (parent.DangerousGetHandle())) name openFlags

            let opened = PosixPrivateNative.privateRegular (handle.DangerousGetHandle())
            use stream = new FileStream(handle, FileAccess.Read)
            let buffer = Array.zeroCreate<byte>(maximum + 1)
            let mutable total = 0
            let mutable count = 1

            try
                while count > 0 && total < buffer.Length do
                    count <- stream.Read(buffer, total, buffer.Length - total)
                    total <- total + count

                PosixPrivateNative.privateRegular (stream.SafeFileHandle.DangerousGetHandle())
                |> ignore

                if not (current parent name opened) then
                    raise (IOException("Private source identity changed while reading."))
                elif total > maximum then
                    Error PrivateFileFailure.TooLarge
                else
                    Ok(Array.truncate total buffer)
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(buffer)))

    let writeNew (path: string) (bytes: byte array) =
        withParent path (fun parent name ->
            let descriptor = int (parent.DangerousGetHandle())
            use handle = PosixPrivateNative.createPrivateHandle descriptor name
            let opened = PosixPrivateNative.stat (handle.DangerousGetHandle())

            try
                PosixPrivateNative.privateRegular (handle.DangerousGetHandle()) |> ignore

                if not (current parent name opened) then
                    raise (IOException("Created private file identity changed."))

                if obj.ReferenceEquals(bytes, null) then
                    raise (ArgumentException("Export bytes are unavailable."))

                use stream = new FileStream(handle, FileAccess.Write)
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)

                PosixPrivateNative.privateRegular (stream.SafeFileHandle.DangerousGetHandle())
                |> ignore

                if not (current parent name opened) then
                    raise (IOException("Created private file identity changed."))

                PosixPrivateNative.syncDirectory descriptor
                Ok()
            with _ ->
                removeCreated parent name opened false
                reraise ())

    let ensureDirectory (path: string) =
        withParent path (fun parent name ->
            let descriptor = int (parent.DangerousGetHandle())
            let created = PosixPrivateNative.createDirectory descriptor name
            let native = PosixPrivateNative.flags ()
            let openFlags = native.Directory ||| native.NoFollow ||| native.CloseOnExec
            use handle = PosixPrivateNative.openHandle descriptor name openFlags
            let opened = PosixPrivateNative.stat (handle.DangerousGetHandle())

            try
                PosixPrivateNative.privateDirectory (handle.DangerousGetHandle()) |> ignore

                let same =
                    PosixPrivateNative.statEntry descriptor name
                    |> Option.exists (PosixPrivateNative.sameFile opened)

                if not same then
                    raise (IOException("Private directory identity changed."))

                if created then
                    PosixPrivateNative.syncDirectory descriptor

                path
            with _ ->
                if created then
                    removeCreated parent name opened true

                reraise ())

    let openExclusive (path: string) =
        withParent path (fun parent name ->
            let descriptor = int (parent.DangerousGetHandle())
            let handle, created = PosixPrivateNative.openLockedHandle descriptor name
            let opened = PosixPrivateNative.stat (handle.DangerousGetHandle())

            try
                PosixPrivateNative.privateRegular (handle.DangerousGetHandle()) |> ignore

                if not (current parent name opened) then
                    raise (IOException("Private lock identity changed."))

                if created then
                    PosixPrivateNative.syncDirectory descriptor

                handle
            with _ ->
                handle.Dispose()

                if created then
                    removeCreated parent name opened false

                reraise ())

    let deletePrivate (path: string) =
        withParent path (fun parent name ->
            let native = PosixPrivateNative.flags ()
            let openFlags = native.NoFollow ||| native.CloseOnExec ||| native.NonBlock
            let descriptor = int (parent.DangerousGetHandle())
            use handle = PosixPrivateNative.openHandle descriptor name openFlags
            let opened = PosixPrivateNative.privateRegular (handle.DangerousGetHandle())

            if not (current parent name opened) then
                raise (IOException("Private file identity changed before removal."))

            PosixPrivateNative.unlink descriptor name false
            PosixPrivateNative.syncDirectory descriptor)
