namespace ClaimCore.Database

open System
open System.Text.Json

/// An unadopted product export is unlocated; verified adoption gives it private custody.
module internal DatabaseManagedCopyLocationPolicy =
    let custody producer (root: JsonElement) =
        let optional (name: string) : string option =
            let value = root.GetProperty(name)

            if value.ValueKind = JsonValueKind.Null then
                None
            else
                value.GetString() |> Option.ofObj

        let location = optional "location"
        let custodian = optional "custodianId"
        let kind = root.GetProperty("kind").GetString()
        let source = root.GetProperty("sourceCaseId").ValueKind

        let privateLocation (path: string) (actor: string) =
            not (String.IsNullOrWhiteSpace actor)
            && IO.Path.IsPathFullyQualified path
            && not (path.Contains("..", StringComparison.Ordinal))

        match producer, location, custodian with
        | "OWNER_ATTESTED", Some path, Some actor when privateLocation path actor ->
            custodian, location
        | "PRODUCT_EXPORT", None, None when kind = "EXPORT" && source = JsonValueKind.String ->
            None, None
        | ("PRODUCT_EXPORT" | "ADOPTED_EXTERNAL"), Some path, Some actor when
            kind = "EXPORT" && source = JsonValueKind.String && privateLocation path actor
            ->
            custodian, location
        | _ -> invalidOp "Copy location custody is invalid."
