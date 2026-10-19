namespace ClaimCore.Web

open System
open System.IO
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.Options
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

    let configure (directory: DirectoryInfo) idle (services: IServiceCollection) =
        services
            .AddDataProtection()
            .SetApplicationName("ClaimCore.Web")
            .PersistKeysToFileSystem(directory)
        |> ignore

        services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>>(fun provider ->
            let protection = provider.GetRequiredService<IDataProtectionProvider>()
            let storage = TicketPersistence(directory, protection)

            PostConfigureOptions<CookieAuthenticationOptions>(
                "Cookies",
                fun options ->
                    options.SessionStore <-
                        OidcTicketStore(idle, options.ExpireTimeSpan, persistence = storage)
            )
            :> IPostConfigureOptions<CookieAuthenticationOptions>)
        |> ignore
