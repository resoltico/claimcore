namespace ClaimCore.Postgres

open System.IO
open System.Reflection
open System.Text
open ClaimCore.Witness

type internal SchemaBaselineDefinition =
    {
        Id: string
        Script: string
        Digest: string
    }

type private SchemaResourceAnchor = class end

/// One reviewed final-state definition, never an upgrade or a compatibility catalog.
module internal SchemaDefinition =
    let private readResource (assembly: Assembly) name =
        match assembly.GetManifestResourceStream(name) |> Option.ofObj with
        | None -> raise (InvalidDataException("An embedded schema baseline resource is missing."))
        | Some stream ->
            use source = stream
            use buffer = new MemoryStream()
            source.CopyTo(buffer)
            buffer.ToArray()

    let private discover () =
        let assembly = typeof<SchemaResourceAnchor>.Assembly
        let marker = readResource assembly "ClaimCore.SchemaBaseline.json"

        let source =
            BaselineSource.assemble assembly marker "ClaimCore.SchemaBaseline.Source."

        {
            Id = source.Id
            Script = UTF8Encoding(false, true).GetString(source.Bytes)
            Digest = source.Digest
        }

    let private definition = lazy (discover ())
    let current () = definition.Value
