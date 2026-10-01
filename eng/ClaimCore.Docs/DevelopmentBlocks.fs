namespace ClaimCore.Docs

open System

[<RequireQualifiedAccess>]
module DevelopmentBlocks =
    let render (root: RepositoryRoot) (processes: IProcessRunner) section =
        processes.Run(
            {
                FileName = "node"
                Arguments = [ "eng/ci/render-development.mjs"; section ]
                WorkingDirectory = root.Path
                Environment = []
                Timeout = TimeSpan.FromMinutes(1.0)
            }
        )
        |> Result.mapError (fun message -> [ Diagnostic.create DiagnosticCode.Invocation message ])
        |> Result.bind (fun output ->
            if output.ExitCode <> 0 || output.StandardError <> "" then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.Invocation
                            "Development renderer failed or wrote stderr."
                    ]
            else
                Ok output.StandardOutput)
