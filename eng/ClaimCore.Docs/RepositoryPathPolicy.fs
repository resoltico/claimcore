namespace ClaimCore.Docs

open System
open System.IO

[<RequireQualifiedAccess>]
module internal RepositoryPathPolicy =
    let private slash (value: string) = value.Replace(char 92, '/')

    let private name (relative: string) =
        let normalized = slash relative
        let separator = normalized.LastIndexOf('/')

        if separator < 0 then
            normalized
        else
            normalized.Substring(separator + 1)

    let private generatedDirectoryNames =
        set
            [
                ".cache"
                ".direnv"
                ".fleet"
                ".git"
                ".history"
                ".idea"
                ".ionide"
                ".local"
                ".vs"
                ".vitest"
                "BenchmarkDotNet.Artifacts"
                "TestResults"
                "bin"
                "coverage"
                "dist"
                "node_modules"
                "obj"
                "playwright-report"
                "test-results"
            ]

    let excludedDirectory (relative: string) =
        let normalized = slash relative

        generatedDirectoryNames.Contains(name normalized)
        || normalized = "artifacts"
        || normalized = "web/artifacts"
        || normalized = "web/blob-report"
        || normalized = "web/playwright/.auth"
        || normalized = "src/ClaimCore.Web/wwwroot"

    let private executableSourceExtensions =
        set
            [
                ".cjs"
                ".cs"
                ".csx"
                ".css"
                ".cts"
                ".fs"
                ".fsi"
                ".fsx"
                ".js"
                ".jsx"
                ".mjs"
                ".mts"
                ".ps1"
                ".psm1"
                ".sh"
                ".ts"
                ".tsx"
            ]

    let private privateSuffixes =
        [
            ".connection"
            ".jks"
            ".key"
            ".keystore"
            ".p12"
            ".pem"
            ".pfx"
            ".pkcs12"
            ".snk"
        ]

    let private privateFile (relative: string) =
        let normalized = slash relative
        let fileName = name relative

        let extension =
            Path.GetExtension(fileName)
            |> Option.ofObj
            |> Option.defaultValue ""
            |> _.ToLowerInvariant()

        let executableSource = executableSourceExtensions.Contains(extension)

        not executableSource
        && (fileName = ".env"
            || (fileName.StartsWith(".env.", StringComparison.Ordinal)
                && fileName <> ".env.example")
            || fileName = ".envrc"
            || (fileName.StartsWith(".envrc.", StringComparison.Ordinal)
                && fileName <> ".envrc.example")
            || fileName = ".pgpass"
            || (fileName = ".npmrc" && normalized <> "web/.npmrc")
            || fileName = ".netrc"
            || fileName = "_netrc"
            || fileName = "pgpass.conf"
            || fileName.StartsWith("bootstrap-credential-", StringComparison.Ordinal)
            || (fileName.StartsWith("claimcore-recovery-", StringComparison.Ordinal)
                && extension = ".json")
            || (privateSuffixes
                |> List.exists (fun suffix ->
                    fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))))

    let private generatedOrLocalFile (relative: string) =
        let normalized = slash relative
        let fileName = name normalized

        let vscodeLocal =
            normalized.StartsWith(".vscode/", StringComparison.Ordinal)
            && normalized <> ".vscode/extensions.json"
            && normalized <> ".vscode/settings.json"

        vscodeLocal
        || fileName = ".DS_Store"
        || fileName = "Desktop.ini"
        || fileName = "Thumbs.db"
        || fileName.StartsWith("._", StringComparison.Ordinal)
        || fileName.StartsWith("npm-debug.log", StringComparison.Ordinal)
        || fileName.StartsWith("pnpm-debug.log", StringComparison.Ordinal)
        || fileName.StartsWith("yarn-debug.log", StringComparison.Ordinal)
        || fileName.StartsWith("yarn-error.log", StringComparison.Ordinal)
        || fileName.EndsWith("~", StringComparison.Ordinal)
        || [
            ".binlog"
            ".coverage"
            ".coveragexml"
            ".nupkg"
            ".orig"
            ".rej"
            ".rsuser"
            ".sln.docstates"
            ".snupkg"
            ".suo"
            ".swo"
            ".swp"
            ".trx"
            ".tsbuildinfo"
            ".user"
            ".userosscache"
           ]
           |> List.exists (fun suffix ->
               fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        || fileName = ".eslintcache"
        || fileName = ".stylelintcache"

    let excludedFile relative =
        privateFile relative || generatedOrLocalFile relative
