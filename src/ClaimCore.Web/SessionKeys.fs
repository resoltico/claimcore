namespace ClaimCore.Web

open System
open System.IO
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.DependencyInjection

module SessionKeys =
    let admit (path: string) =
        let invalid () =
            WebStartupDiagnostics.refuse (
                WebStartupProblem.InvalidSetting WebSetting.KeyRingDirectory
            )

        if not (Path.IsPathFullyQualified path) || not (Directory.Exists path) then
            invalid ()

        try
            let mode = File.GetUnixFileMode(path: string)
            let writable = UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite

            if mode &&& writable <> UnixFileMode.None then
                invalid ()

            DirectoryInfo path
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException -> invalid ()

    let configure (directory: DirectoryInfo) (services: IServiceCollection) =
        services
            .AddDataProtection()
            .SetApplicationName("ClaimCore.Web")
            .PersistKeysToFileSystem(directory)
        |> ignore
