namespace ClaimCore.Web

open System
open System.IO
open System.Security.Cryptography
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.Options
open Microsoft.Extensions.DependencyInjection
open ClaimCore.HostSecurity
open ClaimCore.Postgres

module SessionKeys =
    [<Literal>]
    let internal applicationName = "ClaimCore.Web"

    let admit (path: string) =
        let invalid () =
            WebStartupDiagnostics.refuse (
                WebStartupProblem.InvalidSetting WebSetting.KeyRingDirectory
            )

        if not (Path.IsPathFullyQualified path) || not (Directory.Exists path) then
            invalid ()

        try
            let mode = File.GetUnixFileMode(path: string)

            let writable =
                UnixFileMode.GroupRead
                ||| UnixFileMode.GroupWrite
                ||| UnixFileMode.GroupExecute
                ||| UnixFileMode.OtherRead
                ||| UnixFileMode.OtherWrite
                ||| UnixFileMode.OtherExecute

            let required =
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

            if mode &&& writable <> UnixFileMode.None || mode &&& required <> required then
                invalid ()

            for file in Directory.EnumerateFiles(path, "*.xml") do
                match PrivateFile.read 1048576 file with
                | Ok bytes -> CryptographicOperations.ZeroMemory bytes
                | Error _ -> invalid ()

            DirectoryInfo path
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException -> invalid ()

    let configure
        (directory: DirectoryInfo)
        idle
        (sessions: ISessionStore)
        (services: IServiceCollection)
        =
        services
            .AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToFileSystem(directory)
        |> ignore

        services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>>(fun provider ->
            let protection = provider.GetRequiredService<IDataProtectionProvider>()
            let diagnose (code: string) = Console.Error.WriteLine code

            PostConfigureOptions<CookieAuthenticationOptions>(
                "Cookies",
                fun options ->
                    options.SessionStore <-
                        OidcTicketStore(
                            idle,
                            options.ExpireTimeSpan,
                            sessions,
                            protection,
                            diagnose
                        )
            )
            :> IPostConfigureOptions<CookieAuthenticationOptions>)
        |> ignore
