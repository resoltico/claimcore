namespace ClaimCore.Cli

open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

module Discovery =
    let help () =
        "ClaimCore: help | version [--json] | describe [summary|fields [name]|commands [kind]|endpoints [id]|diagnostics] | schema <name> | call | session"

    let versionJson () =
        let identity = BuildIdentity.current

        JsonSerializer.SerializeToUtf8Bytes(
            {|
                product = identity.Product
                version = identity.Version
                assemblyVersion = identity.AssemblyVersion
                fileVersion = identity.FileVersion
            |}
        )

    let private select
        (bytes: byte array)
        (collection: string)
        (key: string)
        (requested: string option)
        =
        use document = JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes)
        let entries = document.RootElement.GetProperty(collection)

        match requested with
        | None -> Some(Encoding.UTF8.GetBytes(entries.GetRawText()))
        | Some value ->
            entries.EnumerateArray()
            |> Seq.tryFind (fun item ->
                item.GetProperty(key).GetString() |> Option.ofObj = Some value)
            |> Option.map (fun item -> Encoding.UTF8.GetBytes(item.GetRawText()))

    let describe arguments =
        let descriptors = ContractDiscovery.descriptors () |> CanonicalContract.bytes

        match arguments with
        | []
        | [ "summary" ] -> Some descriptors
        | [ "fields" ] -> select descriptors "fields" "name" None
        | [ "fields"; name ] -> select descriptors "fields" "name" (Some name)
        | [ "commands" ] -> select descriptors "commands" "kind" None
        | [ "commands"; kind ] -> select descriptors "commands" "kind" (Some kind)
        | [ "endpoints" ] -> Some(ContractDiscovery.catalogue ())
        | [ "endpoints"; id ] -> select (ContractDiscovery.catalogue ()) "endpoints" "id" (Some id)
        | [ "diagnostics" ] -> Some(ContractDiscovery.presentation ())
        | _ -> None

    let schema arguments =
        match arguments with
        | [ name ] -> ContractDiscovery.schema name |> Option.map CanonicalContract.bytes
        | [ endpoint; direction ] when direction = "request" || direction = "response" ->
            ContractDiscovery.schema (endpoint + "." + direction)
            |> Option.map CanonicalContract.bytes
        | _ -> None
