module ClaimCore.Server.Program

[<EntryPoint>]
let main arguments =
    match Array.toList arguments with
    | "serve" :: rest -> HostProgram.main (List.toArray rest)
    | "probe" :: _ -> HostProgram.main arguments
    | [ "--help" ] -> Administration.Program.main [| "help" |]
    | [ "--version" ] -> Administration.Program.main [| "version" |]
    | _ -> Administration.Program.main arguments
