module ClaimCore.Database.Program

open System
open System.Buffers
open System.Reflection
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Database

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
    printfn "  ClaimCore.Database set-business-zone <canonical-IANA-ID>"
    printfn "  ClaimCore.Database prune [--dry-run] [--settled-retention-days <1-3650>]"
    printfn "                           [--abandoned-retention-days <1-3650>] [--limit <1-1000>]"
    printfn "  ClaimCore.Database describe diagnostics"
    printfn "  ClaimCore.Database help"
    printfn "  ClaimCore.Database version [--json]"
    printfn "Prune defaults: accepted 30 days; revoked 30 days; batch limit 100; deletion enabled."
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
    output.Flush()
    0

let private dispatch argv =
    let output = Console.OpenStandardOutput()
    let errors = Console.OpenStandardError()

    match DatabaseArguments.parse (Array.toList argv) with
    | Error reason -> DatabaseExecution.inputFailure reason errors 64
    | Ok DatabaseCommand.Help ->
        help ()
        Console.Out.Flush()
        0
    | Ok DatabaseCommand.Version ->
        let _, version, _ = identity ()
        Console.Out.WriteLine version
        Console.Out.Flush()
        0
    | Ok DatabaseCommand.VersionJson -> writeVersion ()
    | Ok DatabaseCommand.Diagnostics ->
        output.Write(DatabaseContracts.catalogue ())
        output.Flush()
        0
    | Ok command -> DatabaseExecution.run command output errors

[<EntryPoint>]
let main argv =
    try
        dispatch argv
    with _ ->
        DatabaseExecution.inputFailure
            DatabaseInputProblem.ProcessFailed
            (Console.OpenStandardError())
            70
