module ClaimCore.Docs.Program

open System
open ClaimCore.Docs

[<EntryPoint>]
let main arguments =
    try
        match Repository.discover Environment.CurrentDirectory with
        | Error message ->
            Console.Error.WriteLine(message)
            ExitCode.InvocationFailed
        | Ok root -> Commands.run root (SystemProcessRunner()) (Array.toList arguments)
    with error ->
        Console.Error.WriteLine($"Unexpected documentation-tool failure ({error.GetType().Name}).")
        ExitCode.Unexpected
