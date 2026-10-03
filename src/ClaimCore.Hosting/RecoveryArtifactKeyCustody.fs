namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal RecoveryArtifactKey =
    {
        Id: Guid
        Encryption: byte array
        Mac: byte array
        IssueFrom: DateTimeOffset
        IssueUntil: DateTimeOffset
        VerifyUntil: DateTimeOffset
        MaximumExports: int
    }

/// Each call owns its decoded material and erases it after signing or verification.
type internal RecoveryArtifactKeyRing
    (activeId: Guid, lifetime: TimeSpan, keys: RecoveryArtifactKey list) =
    member _.Lifetime = lifetime

    member _.Active(now: DateTimeOffset) =
        keys
        |> List.tryFind (fun key ->
            key.Id = activeId && key.IssueFrom <= now && now < key.IssueUntil)

    member _.Resolve(id: Guid, now: DateTimeOffset) =
        keys |> List.tryFind (fun key -> key.Id = id && now < key.VerifyUntil)

    interface IDisposable with
        member _.Dispose() =
            for key in keys do
                CryptographicOperations.ZeroMemory(key.Encryption)
                CryptographicOperations.ZeroMemory(key.Mac)

module internal RecoveryArtifactKeyCustody =
    let private invalid () =
        invalidOp "Private recovery artifact key ring is invalid."

    let private exactProperties (expected: string list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = expected.Length && Set.ofList names = Set.ofList expected

    let private material (value: JsonElement) (name: string) =
        let encoded =
            value.GetProperty(name).GetString()
            |> Option.ofObj
            |> Option.defaultWith invalid

        let bytes = Convert.FromBase64String(encoded)

        if
            bytes.Length <> 32
            || bytes |> Array.forall ((=) 0uy)
            || Convert.ToBase64String(bytes) <> encoded
        then
            CryptographicOperations.ZeroMemory(bytes)
            invalid ()

        bytes

    let private validPolicy
        (lifetime: TimeSpan)
        (id: Guid)
        (issueFrom: DateTimeOffset)
        (issueUntil: DateTimeOffset)
        (verifyUntil: DateTimeOffset)
        (maximumExports: int)
        (encryption: byte array)
        (mac: byte array)
        =
        id <> Guid.Empty
        && issueFrom.Offset = TimeSpan.Zero
        && issueUntil.Offset = TimeSpan.Zero
        && verifyUntil.Offset = TimeSpan.Zero
        && issueFrom < issueUntil
        && verifyUntil >= issueUntil + lifetime
        && maximumExports >= 1
        && maximumExports <= 65536
        && not (
            CryptographicOperations.FixedTimeEquals(
                ReadOnlySpan<byte>(encryption),
                ReadOnlySpan<byte>(mac)
            )
        )

    let private exactKeyProperties entry =
        exactProperties
            [
                "id"
                "encryptionBase64"
                "macBase64"
                "issueFrom"
                "issueUntil"
                "verifyUntil"
                "maximumExports"
            ]
            entry

    let private key (lifetime: TimeSpan) (entry: JsonElement) =
        if not (exactKeyProperties entry) then
            invalid ()

        let encryption = material entry "encryptionBase64"

        try
            let mac = material entry "macBase64"

            try
                let issueFrom = entry.GetProperty("issueFrom").GetDateTimeOffset()
                let issueUntil = entry.GetProperty("issueUntil").GetDateTimeOffset()
                let verifyUntil = entry.GetProperty("verifyUntil").GetDateTimeOffset()
                let maximumExports = entry.GetProperty("maximumExports").GetInt32()
                let id = entry.GetProperty("id").GetGuid()

                if
                    not (
                        validPolicy
                            lifetime
                            id
                            issueFrom
                            issueUntil
                            verifyUntil
                            maximumExports
                            encryption
                            mac
                    )
                then
                    invalid ()

                {
                    Id = id
                    Encryption = encryption
                    Mac = mac
                    IssueFrom = issueFrom
                    IssueUntil = issueUntil
                    VerifyUntil = verifyUntil
                    MaximumExports = maximumExports
                }
            with error ->
                CryptographicOperations.ZeroMemory(mac)
                raise error
        with error ->
            CryptographicOperations.ZeroMemory(encryption)
            raise error

    let internal parse (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        let root = document.RootElement

        if
            not (
                exactProperties [ "version"; "activeKeyId"; "artifactLifetimeSeconds"; "keys" ] root
            )
            || root.GetProperty("version").GetInt32() <> 1
        then
            invalid ()

        let lifetimeSeconds = root.GetProperty("artifactLifetimeSeconds").GetInt32()

        if lifetimeSeconds < 60 || lifetimeSeconds > 604800 then
            invalid ()

        let lifetime = TimeSpan.FromSeconds(float lifetimeSeconds)
        let active = root.GetProperty("activeKeyId").GetGuid()
        let entries = root.GetProperty("keys")

        if
            entries.ValueKind <> JsonValueKind.Array
            || entries.GetArrayLength() < 1
            || entries.GetArrayLength() > 64
        then
            invalid ()

        let decoded = ResizeArray<RecoveryArtifactKey>()

        try
            for entry in entries.EnumerateArray() do
                decoded.Add(key lifetime entry)

            let keys = decoded |> Seq.toList

            if
                active = Guid.Empty
                || not (keys |> List.exists (fun item -> item.Id = active))
                || (keys |> List.map _.Id |> Set.ofList |> Set.count) <> keys.Length
            then
                invalid ()

            let materials =
                keys |> List.collect (fun item -> [ item.Encryption; item.Mac ]) |> List.toArray

            for first in 0 .. materials.Length - 1 do
                for second in first + 1 .. materials.Length - 1 do
                    if
                        CryptographicOperations.FixedTimeEquals(
                            ReadOnlySpan<byte>(materials[first]),
                            ReadOnlySpan<byte>(materials[second])
                        )
                    then
                        invalid ()

            new RecoveryArtifactKeyRing(active, lifetime, keys)
        with error ->
            for item in decoded do
                CryptographicOperations.ZeroMemory(item.Encryption)
                CryptographicOperations.ZeroMemory(item.Mac)

            raise error

    let load path =
        match PrivateFileService.readBinary 32768 path with
        | Error _ -> invalidOp "Private recovery artifact key ring is unavailable."
        | Ok bytes ->
            try
                try
                    parse bytes
                with _ ->
                    invalid ()
            finally
                CryptographicOperations.ZeroMemory(bytes)
