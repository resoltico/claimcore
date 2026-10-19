namespace ClaimCore.Web

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.HostSecurity
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.DataProtection

/// One protected server-side ticket per lookup key; this directory belongs to one Web process.
[<Sealed>]
type internal TicketPersistence(directory: DirectoryInfo, protection: IDataProtectionProvider) =
    let privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let root = Path.Combine(directory.FullName, "sessions")

    let validKey (key: string) =
        key.Length = 32
        && key |> Seq.forall (fun character -> Char.IsAsciiHexDigitLower character)

    let path key =
        if not (validKey key) then
            invalidArg (nameof key) "Invalid session lookup key."

        Path.Combine(root, key + ".ticket")

    let protector (key: string) =
        protection.CreateProtector("ClaimCore.Web.SessionTicket").CreateProtector key

    let encode ticket (expiry: DateTimeOffset) (activity: DateTimeOffset) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream)
        writer.Write(expiry.UtcTicks)
        writer.Write(activity.UtcTicks)
        writer.Write(TicketSerializer.Default.Serialize ticket)
        stream.ToArray()

    let decode (bytes: byte array) =
        use stream = new MemoryStream(bytes)
        use reader = new BinaryReader(stream)
        let expiry = DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero)
        let activity = DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero)

        let ticket =
            TicketSerializer.Default.Deserialize(reader.ReadBytes(bytes.Length - 16))

        if isNull ticket then
            raise (InvalidDataException("Invalid session ticket."))

        nonNull ticket, expiry, activity

    do
        Directory.CreateDirectory(
            root,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

        let mode = File.GetUnixFileMode root

        let others =
            UnixFileMode.GroupRead
            ||| UnixFileMode.GroupWrite
            ||| UnixFileMode.GroupExecute
            ||| UnixFileMode.OtherRead
            ||| UnixFileMode.OtherWrite
            ||| UnixFileMode.OtherExecute

        if mode &&& others <> UnixFileMode.None then
            invalidOp "The session ticket directory must be private."

    member _.Remove key = File.Delete(path key)

    member _.Write(key, ticket, expiry, activity) =
        let clear = encode ticket expiry activity
        let temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp")

        try
            if clear.Length > 524288 then
                invalidOp "The OIDC session ticket is too large."

            let encrypted = (protector key).Protect clear

            let options =
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = privateMode
                )

            use stream = new FileStream(temporary, options)
            stream.Write encrypted
            stream.Flush(true)
            stream.Dispose()
            File.Move(temporary, path key, true)
        finally
            CryptographicOperations.ZeroMemory clear
            File.Delete temporary

    member private _.Read key =
        let file = path key

        let encrypted =
            match PrivateFile.read 1048576 file with
            | Ok bytes -> bytes
            | Error _ -> invalidOp "The session ticket file is not private or bounded."

        try
            try
                let clear = (protector key).Unprotect encrypted

                try
                    let ticket, expiry, activity = decode clear
                    Some(key, ticket, expiry, activity)
                finally
                    CryptographicOperations.ZeroMemory clear
            with
            | :? CryptographicException
            | :? InvalidDataException
            | :? EndOfStreamException
            | :? ArgumentException ->
                File.Delete file
                None
        finally
            CryptographicOperations.ZeroMemory encrypted

    member this.Load() =
        let files =
            Directory.EnumerateFiles(root, "*.ticket") |> Seq.truncate 10001 |> Seq.toArray

        if files.Length > 10000 then
            invalidOp "The OIDC session store is full."

        files
        |> Array.choose (fun file ->
            let key = Path.GetFileNameWithoutExtension file |> nonNull
            if validKey key then this.Read key else None)
