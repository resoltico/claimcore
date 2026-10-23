namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

module DiagnosticCodecs =
    open Codec

    let private limit name =
        let minimum =
            if name = "maximumIntegerDigits" || name = "maximumPageSize" then
                1L
            else
                0L

        integer minimum (int64 System.Int32.MaxValue) |> map int int64

    let private limits names =
        let codec =
            (record (fun characters page integer fraction ->
                [
                    "maximumCharacters", characters
                    "maximumPageSize", page
                    "maximumIntegerDigits", integer
                    "maximumFractionalDigits", fraction
                ]
                |> List.choose (fun (name, value) ->
                    value |> Option.map (fun value -> name, value))
                |> Map.ofList)
            : Fields<Map<string, int>, _>)
            |> optionalField
                "maximumCharacters"
                (Map.tryFind "maximumCharacters")
                (limit "maximumCharacters")
            |> optionalField
                "maximumPageSize"
                (Map.tryFind "maximumPageSize")
                (limit "maximumPageSize")
            |> optionalField
                "maximumIntegerDigits"
                (Map.tryFind "maximumIntegerDigits")
                (limit "maximumIntegerDigits")
            |> optionalField
                "maximumFractionalDigits"
                (Map.tryFind "maximumFractionalDigits")
                (limit "maximumFractionalDigits")
            |> build

        codec
        |> constrained
            (Schema.Object(names |> List.map (fun name -> name, (limit name).Schema), []))
            (fun values -> (values |> Map.keys |> Set.ofSeq) = Set.ofList names)

    let private targetKind =
        function
        | Refusal.InvalidInput _ -> 1
        | Refusal.InvalidCursor -> 2
        | _ -> 0

    let private target kind =
        match kind with
        | 1 -> enumeration InputTargets.all |> map Some Option.get
        | 2 -> constant "cursor" |> map (fun () -> Some InputTarget.Cursor) (fun _ -> ())
        | _ -> nullValue |> map (fun () -> None) (fun _ -> ())

    let private revision present =
        if present then
            RequestCodecs.revision |> map Some Option.get
        else
            nullValue |> map (fun () -> None) (fun _ -> ())

    let private group (names, action, hasRevision, location) codes =
        let wire =
            (record (fun code target actual limits -> code, target, actual, limits)
            : Fields<string * InputTarget option * int64 option * Map<string, int>, _>)
            |> field "code" (fun (code, _, _, _) -> code) (RequestCodecs.tokens codes)
            |> ResponseScalars.fixedField "action" (RecommendedActions.token action)
            |> field "target" (fun (_, target, _, _) -> target) (target location)
            |> field "actualRevision" (fun (_, _, actual, _) -> actual) (revision hasRevision)
            |> field "limits" (fun (_, _, _, limits) -> limits) (limits names)
            |> build

        wire
        |> map
            (fun (code, target, actual, limits) ->
                Refusals.fromDiagnostic code target actual limits)
            (fun reason ->
                (Refusals.row reason).Code,
                (match reason with
                 | Refusal.InvalidInput(target, _) -> Some target
                 | Refusal.InvalidCursor -> Some InputTarget.Cursor
                 | _ -> None),
                Refusals.actualRevision reason,
                Refusals.parameters reason |> Map.ofList)

    let diagnostic =
        Refusals.all
        |> List.groupBy (fun (reason, row) ->
            Refusals.parameters reason |> List.map fst |> List.sort,
            row.Action,
            Refusals.actualRevision reason |> Option.isSome,
            targetKind reason)
        |> List.map (fun (key, rows) ->
            let codes = rows |> List.map (fun (_, row) -> row.Code)

            branch
                (fun value -> value)
                (fun reason ->
                    if List.contains (Refusals.row reason).Code codes then
                        Some reason
                    else
                        None)
                (group key codes))
        |> choice "Diagnostic"

    let private faultGroup action faults =
        record (fun fault -> fault)
        |> field
            "code"
            (fun value -> value)
            (enumeration (faults |> List.map (fun fault -> fault, CoreFaults.token fault)))
        |> ResponseScalars.fixedField "action" (RecommendedActions.token action)
        |> build

    let fault =
        CoreFaults.all
        |> List.map fst
        |> List.groupBy CoreFaults.action
        |> List.map (fun (action, faults) ->
            branch
                (fun value -> value)
                (fun value -> if List.contains value faults then Some value else None)
                (faultGroup action faults))
        |> choice "Fault"
