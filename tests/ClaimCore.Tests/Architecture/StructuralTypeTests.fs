module ClaimCore.Tests.Architecture.StructuralTypeTests

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Tests.Architecture.DeclarationFixtures

let private representation = BindingFlags.Public ||| BindingFlags.NonPublic
let private exemptions: Map<string * string, string> = Map.empty

let rec private typeSignature (kind: Type) =
    if kind.IsGenericParameter then
        "parameter:" + string kind.GenericParameterPosition
    elif kind.IsArray then
        "array:"
        + string (kind.GetArrayRank())
        + ":"
        + typeSignature (kind.GetElementType() |> nonNull)
    elif kind.IsGenericType then
        let definition = kind.GetGenericTypeDefinition()

        (definition.FullName |> nonNull)
        + "<"
        + (kind.GetGenericArguments() |> Array.map typeSignature |> String.concat ",")
        + ">"
    else
        kind.FullName |> nonNull

let private signature (kind: Type) =
    if FSharpType.IsRecord(kind, representation) then
        FSharpType.GetRecordFields(kind, representation)
        |> Array.map (fun field -> field.Name + ":" + typeSignature field.PropertyType)
        |> String.concat ";"
        |> (+) "record:"
        |> Some
    elif FSharpType.IsUnion(kind, representation) then
        FSharpType.GetUnionCases(kind, representation)
        |> Array.sortBy _.Tag
        |> Array.map (fun case ->
            case.Name
            + "("
            + (case.GetFields()
               |> Array.map (fun field -> typeSignature field.PropertyType)
               |> String.concat ",")
            + ")")
        |> String.concat ";"
        |> (+) "union:"
        |> Some
    else
        None

let private pairs types =
    types
    |> List.choose (fun kind ->
        signature kind |> Option.map (fun shape -> shape, kind.FullName |> nonNull))
    |> List.groupBy fst
    |> List.collect (fun (_, values) ->
        let names = values |> List.map snd |> List.distinct |> List.sort

        [
            for index, left in List.indexed names do
                for right in List.skip (index + 1) names do
                    yield left, right
        ])

let private requireDistinct exceptions types =
    let duplicates =
        pairs types |> List.filter (fun pair -> not (Map.containsKey pair exceptions))

    Expect.isEmpty duplicates ("Structurally duplicate declarations: " + sprintf "%A" duplicates)

let private controls () =
    let identical = [ typeof<DuplicateFirst>; typeof<DuplicateSecond> ]
    Expect.hasLength (pairs identical) 1 "The compiled unions have exactly one duplicate pair"

    Expect.throws
        (fun () -> requireDistinct Map.empty identical)
        "The same signature gate rejects the isolated compiled duplicate"

    requireDistinct Map.empty [ typeof<DuplicateFirst>; typeof<Distinct> ]

    let records =
        [ typedefof<DuplicateRecordFirst<_>>; typedefof<DuplicateRecordSecond<_>> ]

    Expect.hasLength (pairs records) 1 "Generic record parameter names do not hide equal structure"

    Expect.throws
        (fun () -> requireDistinct Map.empty records)
        "The same gate rejects compiled duplicate generic records"

    requireDistinct Map.empty [ typedefof<DuplicateRecordFirst<_>>; typedefof<DistinctRecord<_>> ]

let private sourceDeclaration (kind: Type) =
    let parent = kind.DeclaringType

    let unionSubclass =
        not (obj.ReferenceEquals(parent, null))
        && FSharpType.IsUnion(parent |> nonNull, representation)

    (kind.IsPublic || kind.IsNestedPublic) && not unionSubclass

let private declarations () =
    controls ()

    let types =
        [ "ClaimCore.Domain"; "ClaimCore.Application"; "ClaimCore.Protocol" ]
        |> List.collect (fun name ->
            Assembly.Load(name).GetTypes() |> Array.filter sourceDeclaration |> Array.toList)

    let names = types |> List.map (fun kind -> kind.FullName |> nonNull) |> Set.ofList

    for KeyValue((left, right), reason) in exemptions do
        Expect.isTrue
            (Set.contains left names && Set.contains right names)
            "An exemption identifies two current declarations"

        Expect.isNonEmpty reason "A duplicate exemption explains independent domain meaning"

    requireDistinct exemptions types

[<Tests>]
let tests =
    testCase "public product records and unions have distinct structural signatures" declarations
