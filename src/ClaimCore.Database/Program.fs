module ClaimCore.Database.Program

open ClaimCore.Contract

open System
open ClaimCore.Application
open ClaimCore.Postgres

[<EntryPoint>]
let main arguments =
    let bytes, code, deliveryUnknown =
        try
            BuildIdentity.requireCompatibleAssembly typeof<Administration>.Assembly

            match Inputs.command (Array.toList arguments) with
            | None -> let bytes, code = Replies.tag "REFUSED" 2 in bytes, code, false
            | Some command ->
                let bytes, code = Execution.run command |> _.GetAwaiter().GetResult()

                let mutates =
                    match command with
                    | Initialize _
                    | ProvisionOwner -> true
                    | _ -> false

                bytes, code, (code = 4 || (mutates && code = 0))
        with _ ->
            let bytes, code = Replies.tag "FAILED" 3
            bytes, code, false

    try
        Replies.write (Console.OpenStandardOutput()) (bytes, code, deliveryUnknown)
    with _ ->
        if deliveryUnknown then 4 else 3
