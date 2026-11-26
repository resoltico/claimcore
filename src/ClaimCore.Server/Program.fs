module ClaimCore.Server.Program

[<EntryPoint>]
let main arguments =
    match Array.toList arguments with
    | "serve" :: rest -> HostProgram.main (List.toArray rest)
    | [ "validate-browser-assets"; directory ] ->
        HostProgram.main [| "validate-browser-assets"; directory |]
    | "probe" :: _ -> HostProgram.main arguments
    | [ "--help" ] -> Administration.Program.main [| "help" |]
    | [ "--version" ] -> Administration.Program.main [| "version" |]
    | _ -> Administration.Program.main arguments
