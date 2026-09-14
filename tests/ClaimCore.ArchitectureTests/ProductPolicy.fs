module ClaimCore.ArchitectureTests.ProductPolicy

/// The reviewed maximum direct product graph. Removed edges require no compatibility allowance.
let permissions =
    [
        "Domain", []
        "RecordFormat", [ "Domain" ]
        "Application", [ "Domain"; "RecordFormat" ]
        "Contracts", [ "Domain"; "Application" ]
        "HostSecurity", []
        "Postgres", [ "Domain"; "RecordFormat"; "Application" ]
        "Cli", [ "Domain"; "Application"; "Contracts"; "Postgres"; "HostSecurity" ]
        "Web", [ "Domain"; "Application"; "Contracts"; "Postgres"; "HostSecurity" ]
        "Database", [ "Postgres"; "HostSecurity" ]
    ]

let name part = "ClaimCore." + part
let names = permissions |> List.map (fst >> name)

let allowed =
    permissions
    |> List.map (fun (part, targets) -> name part, List.map name targets)
    |> Map.ofList
