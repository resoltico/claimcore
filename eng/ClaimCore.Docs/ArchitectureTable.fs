namespace ClaimCore.Docs

/// Renders the reviewed component contract so the architecture document cannot drift from the
/// manifest the compiled architecture suite enforces.
[<RequireQualifiedAccess>]
module ArchitectureTable =
    let private prefix = "ClaimCore."

    let private cell (value: string) = value.Replace("|", "\\|")

    let private short (value: string) =
        if value.StartsWith(prefix, System.StringComparison.Ordinal) then
            value.Substring(prefix.Length)
        else
            value

    let private row (item: ArchitectureComponent) =
        let dependencies =
            if item.DependsOn.IsEmpty then
                "none"
            else
                item.DependsOn
                |> List.map (fun target -> "`" + cell (short target) + "`")
                |> String.concat ", "

        "| `"
        + cell (short item.Name)
        + "` | "
        + cell item.Layer
        + " | "
        + cell item.Role
        + " | "
        + dependencies
        + " |"

    let private table (components: ArchitectureComponent list) =
        "| Component | Layer | Responsibility | Direct dependencies |\n"
        + "|---|---|---|---|\n"
        + (components |> List.map row |> String.concat "\n")
        + "\n"

    let render (root: RepositoryRoot) =
        ArchitectureManifest.load root
        |> Result.bind (fun components ->
            match components |> List.filter (fun item -> item.Tier = "product") with
            | [] -> Error "The architecture manifest classifies no product component."
            | product -> Ok(table product))
