module ClaimCore.Database.Program

open System
open System.Buffers
open System.Reflection
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres

let private identity () =
    let assembly = Assembly.GetExecutingAssembly()

    let product =
        assembly.GetCustomAttribute<AssemblyProductAttribute>()
        |> Option.ofObj
        |> Option.map _.Product
        |> Option.defaultWith (fun () -> invalidOp "Compiled product identity is missing.")

    let version =
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        |> Option.ofObj
        |> Option.map _.InformationalVersion
        |> Option.defaultWith (fun () -> invalidOp "Compiled release identity is missing.")

    let assemblyVersion =
        assembly.GetName().Version
        |> Option.ofObj
        |> Option.map string
        |> Option.defaultWith (fun () -> invalidOp "Compiled assembly identity is missing.")

    product, version, assemblyVersion

let private help () =
    let _, version, _ = identity ()
    printfn "ClaimCore.Database %s — schema and recovery-retention administration" version
    printfn "  ClaimCore.Database migrate"
    printfn "  ClaimCore.Database prune [--dry-run] [--settled-retention-days <1-3650>]"
    printfn "                           [--abandoned-retention-days <1-3650>] [--limit <1-1000>]"
    printfn "  ClaimCore.Database help"
    printfn "  ClaimCore.Database version [--json]"
    printfn "Prune defaults: settled 30 days; dismissed 30 days; batch limit 100; deletion enabled."
    printfn "Set CLAIMCORE_ADMIN_CONNECTION_FILE to an owner-private schema-owner connection file."

let private writeVersion () =
    let product, version, assemblyVersion = identity ()
    let buffer = ArrayBufferWriter<byte>()
    use writer = new Utf8JsonWriter(buffer)
    writer.WriteStartObject()
    writer.WriteString("application", product)
    writer.WriteString("version", version)
    writer.WriteString("assemblyVersion", assemblyVersion)
    writer.WriteEndObject()
    writer.Flush()
    let output = Console.OpenStandardOutput()
    output.Write(buffer.WrittenSpan)
    output.WriteByte(byte '\n')
    0

let private ownerConnection path =
    let value =
        match PrivateFileService.readUtf8Text 8192 path with
        | Ok text -> text.Trim()
        | Error _ ->
            invalidArg
                (nameof path)
                "The owner connection file must be a bounded private UTF-8 regular file."

    if String.IsNullOrWhiteSpace(value) then
        invalidArg (nameof path) "The owner connection file is empty."

    value

let private usage () =
    eprintfn "Use ClaimCore.Database help for supported arguments."

let private positiveBounded name maximum (value: string) =
    match Int32.TryParse(value) with
    | true, parsed when parsed >= 1 && parsed <= maximum -> Ok parsed
    | _ -> Error(sprintf "%s must be an integer from 1 through %d." name maximum)

let private pruneOptions arguments =
    let rec read (options: PreparationPruneOptions) seen remaining =
        match remaining with
        | [] -> Ok options
        | "--dry-run" :: tail when not (Set.contains "--dry-run" seen) ->
            read { options with DryRun = true } (Set.add "--dry-run" seen) tail
        | "--settled-retention-days" :: value :: tail when
            not (Set.contains "--settled-retention-days" seen)
            ->
            match positiveBounded "--settled-retention-days" 3650 value with
            | Ok days ->
                read
                    { options with
                        SettledRetentionDays = days
                    }
                    (Set.add "--settled-retention-days" seen)
                    tail
            | Error message -> Error message
        | "--abandoned-retention-days" :: value :: tail when
            not (Set.contains "--abandoned-retention-days" seen)
            ->
            match positiveBounded "--abandoned-retention-days" 3650 value with
            | Ok days ->
                read
                    { options with
                        AbandonedRetentionDays = days
                    }
                    (Set.add "--abandoned-retention-days" seen)
                    tail
            | Error message -> Error message
        | "--limit" :: value :: tail when not (Set.contains "--limit" seen) ->
            match positiveBounded "--limit" 1000 value with
            | Ok limit -> read { options with BatchLimit = limit } (Set.add "--limit" seen) tail
            | Error message -> Error message
        | option :: _ -> Error(sprintf "Unknown, incomplete, or repeated option: %s" option)

    read PreparationPruneOptions.defaults Set.empty arguments

let private withOwner action =
    match
        Environment.GetEnvironmentVariable("CLAIMCORE_ADMIN_CONNECTION_FILE")
        |> Option.ofObj
    with
    | None -> Error "Missing CLAIMCORE_ADMIN_CONNECTION_FILE."
    | Some path ->
        let value = ownerConnection path
        action value
        Ok()

[<EntryPoint>]
let main argv =
    try
        match argv |> Array.toList with
        | [ "help" ]
        | [ "--help" ] ->
            help ()
            0
        | [ "version" ]
        | [ "--version" ] ->
            let _, version, _ = identity ()
            printfn "%s" version
            0
        | [ "version"; "--json" ] -> writeVersion ()
        | [ "migrate" ] ->
            match withOwner Migrations.apply with
            | Ok() ->
                printfn
                    "Ordered database migrations installed or verified on the required PostgreSQL baseline."

                0
            | Error message ->
                eprintfn "%s" message
                3
        | "prune" :: options ->
            match pruneOptions options with
            | Error message ->
                eprintfn "%s" message
                usage ()
                64
            | Ok parsed ->
                match
                    withOwner (fun connection ->
                        PreparationPruning.prune connection parsed |> ignore)
                with
                | Ok() ->
                    printfn
                        "Bounded preparation retention maintenance completed; counts are retained in the database audit journal."

                    0
                | Error message ->
                    eprintfn "%s" message
                    3
        | _ ->
            usage ()
            64
    with error ->
        eprintfn
            "Database maintenance failed (%s). Inspect database configuration and version/checksum. No automatic repair or downgrade was attempted."
            (error.GetType().Name)

        3
