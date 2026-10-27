namespace ClaimCore.ProtocolGeneration

open ClaimCore.Domain
open ClaimCore.Protocol

open System.Text.Json
open ClaimCore.Json

/// Emits specialized checks for the finite codec vocabulary; no runtime schema interpretation.
module ClientValidation =
    let private quote (text: string) = JsonSerializer.Serialize text
    let private identifier (name: string) = name.Replace('.', '_')

    let private runtime =
        """
export type WireIssue = ReadonlyArray<string>;
type WireCheck = (value: unknown, draft: boolean, path: WireIssue) => WireIssue | null;
export const isObject = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
export const isRepresentable = (value: string): boolean => Array.from(value).every(character => {
  const scalar = character.codePointAt(0)!;
  return scalar !== 0 && !(scalar >= 0xd800 && scalar <= 0xdfff) && !(scalar >= 0xfdd0 && scalar <= 0xfdef) && (scalar & 0xffff) < 0xfffe;
});
export const isGregorianDate = (value: string): boolean => {
  const match = /^([0-9]{4})-([0-9]{2})-([0-9]{2})$/u.exec(value);
  if (match === null) { return false; }
  const year = Number(match[1]); const month = Number(match[2]); const day = Number(match[3]);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= (days[month - 1] ?? 0);
};
const wireLength = (value: string, minimum: number | null, maximum: number | null): boolean => {
  const { length } = Array.from(value);
  return (minimum === null || length >= minimum) && (maximum === null || length <= maximum);
};
const wireFormat = (value: string, format: string | null): boolean => {
  if (format === "uuid") { return /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/u.test(value); }
  if (format === "date") { return isGregorianDate(value); }
  if (format === "date-time") {
    return isGregorianDate(value.slice(0,10)) && /^[0-9]{4}-[0-9]{2}-[0-9]{2}T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]\.[0-9]{6}Z$/u.test(value);
  }
  return true;
};
const wireString = (minimum: number | null, maximum: number | null, pattern: RegExp | null, format: string | null): WireCheck => (value,draft,path) => {
  if (typeof value !== "string" || !isRepresentable(value)) { return path; }
  if (draft) { return null; }
  return wireLength(value,minimum,maximum) && (pattern === null || pattern.test(value)) && wireFormat(value,format) ? null : path;
};
const wireInteger = (minimum: number,maximum: number): WireCheck => (value,draft,path) => typeof value === "number" && Number.isSafeInteger(value) && (draft || (value >= minimum && value <= maximum)) ? null : path;
const wireBoolean: WireCheck = (value,_draft,path) => typeof value === "boolean" ? null : path;
const wireNull: WireCheck = (value,_draft,path) => value === null ? null : path;
const wireConstant = (constant: string): WireCheck => (value,_draft,path) => value === constant ? null : path;
const wireObject = (fields: ReadonlyArray<readonly [string,boolean,WireCheck]>): WireCheck => (value,draft,path) => {
  if (!isObject(value)) { return path; }
  for (const [name,optional,check] of fields) {
    if (Object.hasOwn(value,name)) { const issue = check(value[name],draft,[...path,name]); if (issue !== null) { return issue; } }
    else if (!optional) { return [...path,name]; }
  }
  const extra = Object.keys(value).find(name => !fields.some(field => field[0] === name));
  return extra === undefined ? null : [...path,extra];
};
const wireArray = (check: WireCheck, minimum: number,maximum: number): WireCheck => (value,draft,path) => {
  if (!Array.isArray(value) || value.length < minimum || value.length > maximum) { return path; }
  for (let index=0;index<value.length;index++) {
    const issue=check(value[index],draft,[...path,String(index)]);
    if (issue!==null) { return issue; }
  }
  return null;
};
const wireUnion = (checks: ReadonlyArray<WireCheck>, selector: string | null, branches: Readonly<Record<string,WireCheck>>): WireCheck => (value,draft,path) => {
  const issues = checks.map(check => check(value,draft,path));
  if (issues.filter(issue => issue===null).length === 1) { return null; }
  if (selector !== null && isObject(value) && typeof value[selector] === "string") {
    const branch = branches[value[selector]];
    if (branch !== undefined) { return branch(value,draft,path) ?? path; }
    return [...path,selector];
  }
  return path;
};
"""

    let private optionNumber value =
        value |> Option.map string |> Option.defaultValue "null"

    let rec private check =
        function
        | Schema.Ref name -> "check_" + identifier name
        | Schema.Null -> "wireNull"
        | Schema.Boolean -> "wireBoolean"
        | Schema.Const value -> "wireConstant(" + quote value + ")"
        | Schema.Integer(minimum, maximum) -> $"wireInteger({minimum},{maximum})"
        | Schema.String(pattern, format, minimum, maximum) ->
            stringCheck pattern format minimum maximum
        | Schema.Array(items, minimum, maximum) -> $"wireArray({check items},{minimum},{maximum})"
        | Schema.Object(properties, optional) ->
            properties
            |> List.map (fun (name, schema) ->
                "["
                + quote name
                + ","
                + (if List.contains name optional then "true" else "false")
                + ","
                + check schema
                + "]")
            |> String.concat ","
            |> fun fields -> "wireObject([" + fields + "])"
        | Schema.OneOf(variants, discriminator) -> unionCheck variants discriminator

    and private stringCheck pattern format minimum maximum =
        if
            format
            |> Option.exists (fun value ->
                not (List.contains value [ "uuid"; "date"; "date-time" ]))
        then
            invalidOp "The client emitter does not support this string format."

        let pattern =
            pattern
            |> Option.map (fun value -> "/" + value.Replace("/", "\\/") + "/u")
            |> Option.defaultValue "null"

        "wireString("
        + optionNumber minimum
        + ","
        + optionNumber maximum
        + ","
        + pattern
        + ","
        + (format |> Option.map quote |> Option.defaultValue "null")
        + ")"

    and private unionCheck variants discriminator =
        let selector, mapping =
            match discriminator with
            | None -> "null", "{}"
            | Some(name, mapping) ->
                quote name,
                (mapping
                 |> List.map (fun (tag, target) -> quote tag + ":check_" + identifier target)
                 |> String.concat ","
                 |> fun fields -> "{" + fields + "}")

        "wireUnion(["
        + (List.map check variants |> String.concat ",")
        + "],"
        + selector
        + ","
        + mapping
        + ")"

    let private endpoints response =
        Api.all
        |> List.map (fun endpoint ->
            let schema =
                (if response then
                     endpoint.OutputSchema
                 else
                     endpoint.InputSchema)

            quote (Api.id endpoint) + ":" + check schema)
        |> String.concat ","
        |> fun fields -> "{" + fields + "}"

    let private hosts () =
        ClaimCore.Protocol.HostFailureReason.all
        |> List.map ClaimCore.Protocol.HostFailureReason.status
        |> List.distinct
        |> List.map (fun status ->
            string status
            + ":"
            + check (ClaimCore.Protocol.HostFailureCodecs.responseSchema status))
        |> String.concat ","
        |> fun fields -> "{" + fields + "}"

    let source () =
        let definitions =
            Map.fold
                (fun state name value -> Map.add name value state)
                OpenApi.definitions
                CatalogCodecs.codec.Definitions

        let roots =
            Api.all
            |> List.collect (fun endpoint -> [ endpoint.InputSchema; endpoint.OutputSchema ])

        let reachable =
            SchemaJson.reachable definitions (CatalogCodecs.codec.Schema :: roots)

        let functions =
            reachable
            |> Map.toList
            |> List.map (fun (name, schema) ->
                "function check_"
                + identifier name
                + "(value:unknown,draft:boolean,path:WireIssue):WireIssue|null { return ("
                + check schema
                + ")(value,draft,path); }")

        [
            runtime
            yield! functions
            "const requestChecks: Readonly<Record<EndpointId,WireCheck>> = "
            + endpoints false
            + ";"
            "const hostChecks: Readonly<Record<number,WireCheck>> = " + hosts () + ";"
            "export const isHostFailure = (value:unknown,status:number):value is {readonly hostFailure:HostFailure} => hostChecks[status]?.(value,false,[]) === null;"
            "const responseChecks: Readonly<Record<EndpointId,WireCheck>> = "
            + endpoints true
            + ";"
            "export const requestIssue = (endpoint:EndpointId,value:unknown):WireIssue|null => requestChecks[endpoint](value,false,[]);"
            "export const isRequest = <E extends EndpointId>(endpoint:E,value:unknown):value is RequestByEndpoint[E] => requestIssue(endpoint,value) === null;"
            "export const isResponse = <E extends EndpointId>(endpoint:E,value:unknown):value is ResponseByEndpoint[E] => responseChecks[endpoint](value,false,[]) === null;"
            "export const buildRequest = <E extends EndpointId>(endpoint:E,value:unknown):RequestByEndpoint[E] => { const matches = (candidate:unknown):candidate is RequestByEndpoint[E] => requestChecks[endpoint](candidate,true,[]) === null; if (!matches(value)) { throw new Error(\"Invalid endpoint draft structure.\"); } return value; };"
            "export const isCatalogDocument = (value:unknown):value is CatalogDocument => check_Presentation_CatalogDocument(value,false,[]) === null;"
        ]
        |> String.concat "\n"
