namespace ClaimCore.Database

open System
open System.Globalization
open ClaimCore.Postgres

[<RequireQualifiedAccess>]
type DatabaseOption =
    | SettledRetention
    | AbandonedRetention
    | Limit
    | DryRun

module DatabaseOptions =
    let all =
        [
            DatabaseOption.SettledRetention
            DatabaseOption.AbandonedRetention
            DatabaseOption.Limit
            DatabaseOption.DryRun
        ]

    let token =
        function
        | DatabaseOption.SettledRetention -> "--settled-retention-days"
        | DatabaseOption.AbandonedRetention -> "--abandoned-retention-days"
        | DatabaseOption.Limit -> "--limit"
        | DatabaseOption.DryRun -> "--dry-run"

    let maximum =
        function
        | DatabaseOption.SettledRetention
        | DatabaseOption.AbandonedRetention -> 3650
        | DatabaseOption.Limit -> 1000
        | DatabaseOption.DryRun -> 1

[<RequireQualifiedAccess>]
type DatabaseInputProblem =
    | UnsupportedInvocation
    | UnknownOption
    | MissingOptionValue of DatabaseOption
    | RepeatedOption of DatabaseOption
    | OptionOutOfRange of DatabaseOption
    | ConnectionSettingMissing
    | ConnectionFileRefused
    | ConnectionFileEmpty
    | ProcessFailed
    | OutputDeliveryFailed

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type DatabaseCommand =
    | Help
    | Version
    | VersionJson
    | Diagnostics
    | Migrate
    | SetBusinessZone of string
    | Prune of PreparationPruneOptions

module DatabaseArguments =
    let private update options setting value =
        match setting with
        | DatabaseOption.SettledRetention ->
            { options with
                SettledRetentionDays = value
            }
        | DatabaseOption.AbandonedRetention ->
            { options with
                AbandonedRetentionDays = value
            }
        | DatabaseOption.Limit -> { options with BatchLimit = value }
        | DatabaseOption.DryRun -> { options with DryRun = true }

    let private numeric setting (raw: string) =
        match Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, value when value >= 1 && value <= DatabaseOptions.maximum setting -> Ok value
        | _ -> Error(DatabaseInputProblem.OptionOutOfRange setting)

    let private prune arguments =
        let rec read options seen remaining =
            match remaining with
            | [] -> Ok(DatabaseCommand.Prune options)
            | raw :: tail ->
                match DatabaseOptions.all |> List.tryFind (DatabaseOptions.token >> (=) raw) with
                | None -> Error DatabaseInputProblem.UnknownOption
                | Some setting when Set.contains setting seen ->
                    Error(DatabaseInputProblem.RepeatedOption setting)
                | Some DatabaseOption.DryRun ->
                    read { options with DryRun = true } (Set.add DatabaseOption.DryRun seen) tail
                | Some setting ->
                    match tail with
                    | [] -> Error(DatabaseInputProblem.MissingOptionValue setting)
                    | value :: _ when value.StartsWith("--", StringComparison.Ordinal) ->
                        Error(DatabaseInputProblem.MissingOptionValue setting)
                    | value :: rest ->
                        numeric setting value
                        |> Result.bind (fun number ->
                            read (update options setting number) (Set.add setting seen) rest)

        read PreparationPruneOptions.defaults Set.empty arguments

    let parse =
        function
        | [ "help" ]
        | [ "--help" ] -> Ok DatabaseCommand.Help
        | [ "version" ]
        | [ "--version" ] -> Ok DatabaseCommand.Version
        | [ "version"; "--json" ] -> Ok DatabaseCommand.VersionJson
        | [ "describe"; "diagnostics" ] -> Ok DatabaseCommand.Diagnostics
        | [ "migrate" ] -> Ok DatabaseCommand.Migrate
        | [ "set-business-zone"; zone ] -> Ok(DatabaseCommand.SetBusinessZone zone)
        | "prune" :: options -> prune options
        | _ -> Error DatabaseInputProblem.UnsupportedInvocation

    let commandToken =
        function
        | DatabaseCommand.Migrate -> "MIGRATE"
        | DatabaseCommand.SetBusinessZone _ -> "SET_BUSINESS_ZONE"
        | DatabaseCommand.Prune _ -> "PRUNE"
        | DatabaseCommand.Help -> "HELP"
        | DatabaseCommand.Version
        | DatabaseCommand.VersionJson -> "VERSION"
        | DatabaseCommand.Diagnostics -> "DESCRIBE_DIAGNOSTICS"
