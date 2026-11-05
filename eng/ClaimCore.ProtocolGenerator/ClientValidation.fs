namespace ClaimCore.ProtocolGeneration

open ClaimCore.Domain
open ClaimCore.Protocol

open System.Text.Json
open ClaimCore.Json

/// Emits specialized checks for the finite codec vocabulary; no runtime schema interpretation.
module ClientValidation =
    let private quote (text: string) = JsonSerializer.Serialize text
    let private identifier (name: string) = name.Replace('.', '_')

    let private regexLiteral (pattern: string) =
        let output = System.Text.StringBuilder()
        let mutable inClass = false
        let mutable escaped = false

        for character in pattern do
            if escaped then
                output.Append(character) |> ignore
                escaped <- false
            else
                match character with
                | '\\' ->
                    output.Append(character) |> ignore
                    escaped <- true
                | '[' ->
                    output.Append(character) |> ignore
                    inClass <- true
                | ']' ->
                    output.Append(character) |> ignore
                    inClass <- false
                | '/' when not inClass -> output.Append("\\/") |> ignore
                | _ -> output.Append(character) |> ignore

        "/" + output.ToString() + "/u"

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
const wireUtf8Bytes = (value: string): number => Array.from(value).reduce((bytes, character) => {
  const scalar=character.codePointAt(0)!;
  if (scalar<=0x7f) { return bytes+1; }
  if (scalar<=0x7ff) { return bytes+2; }
  if (scalar<=0xffff) { return bytes+3; }
  return bytes+4;
},0);
const wireString = (minimum: number | null, maximum: number | null, pattern: RegExp | null, format: string | null, maximumUtf8Bytes: number | null = null): WireCheck => (value,draft,path) => {
  if (typeof value !== "string" || !isRepresentable(value)) { return path; }
  if (draft) { return null; }
  return wireLength(value,minimum,maximum) && (pattern === null || pattern.test(value)) && wireFormat(value,format) && (maximumUtf8Bytes === null || wireUtf8Bytes(value)<=maximumUtf8Bytes) ? null : path;
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
const wireEnumPartition = (check: WireCheck, values: ReadonlyArray<string>, selected: string, rejected: string, key: string): WireCheck => (value,draft,path) => {
  const issue = check(value,draft,path);
  if (issue !== null) { return issue; }
  if (!isObject(value) || !Array.isArray(value[selected]) || !Array.isArray(value[rejected])) { return path; }
  const offered = value[selected] as unknown[];
  const unavailable = (value[rejected] as unknown[]).map(item => isObject(item) ? item[key] : undefined);
  if (new Set(unavailable).size !== unavailable.length || values.some(token => offered.includes(token) === unavailable.includes(token))) { return [...path,rejected]; }
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
// Relational response grammar: selectors reference only already supplied disclosed differences.
const hasTimelineDisclosureShape = (value:unknown,structural:WireCheck,draft:boolean,path:WireIssue):value is TimelineChanges_DISCLOSED => structural(value,draft,path)===null;
const timelineFacetOrder: Readonly<Record<TimelineFacet,number>> = {ACTION:0,DUE:1,STATE:2};
const timelineFacetAvailable = (facet:TimelineFacet,change:ReminderDifference):boolean => {
  switch(facet) { case "ACTION":return change.action!==null;case "DUE":return change.due!==null;case "STATE":return change.state!==null; }
};
const timelineFacetsValid = (facets:ReadonlyArray<TimelineFacet>,change:ReminderDifference):boolean => {
  if(facets.length===0) { return false; } let previous=-1;
  for(const facet of facets) { const rank=timelineFacetOrder[facet];if(rank<=previous||!timelineFacetAvailable(facet,change)) { return false; } previous=rank; }
  return true;
};
const timelineReferenceValid = (reference:TimelineHighlight,item:TimelineChange|undefined):boolean => {
  if(item===undefined) { return false; }
  if(reference.kind==="CHANGE") { return item.kind!=="REMINDER"; }
  return item.kind==="REMINDER"&&timelineFacetsValid(reference.facets,item.change);
};
const timelineReferencesIssue = (disclosure:TimelineChanges_DISCLOSED,path:WireIssue):WireIssue|null => {
  let previous=-1;
  for(const [ordinal,reference] of disclosure.overview.highlights.entries()) {
    if(reference.index<=previous||!timelineReferenceValid(reference,disclosure.items.at(reference.index))) { return [...path,"overview","highlights",String(ordinal)]; }
    previous=reference.index;
  }
  return null;
};
const wireTimelineDisclosure = (structural:WireCheck):WireCheck => (value,draft,path) => {
  if(!hasTimelineDisclosureShape(value,structural,draft,path)) { return path; }
  return draft?null:timelineReferencesIssue(value,path);
};
const resolveTimelineHighlight = (disclosure:TimelineChanges_DISCLOSED,reference:TimelineHighlight):TimelineChange => {
  const item=disclosure.items.at(reference.index);
  if(item===undefined) { throw new Error("Invalid timeline highlight reference."); }
  if(reference.kind==="CHANGE") { return item; }
  if(item.kind!=="REMINDER") { throw new Error("Invalid reminder highlight reference."); }
  return {kind:"REMINDER",change:{reminderId:item.change.reminderId,
    action:reference.facets.includes("ACTION")?item.change.action:null,
    due:reference.facets.includes("DUE")?item.change.due:null,
    state:reference.facets.includes("STATE")?item.change.state:null}};
};
/** Resolve Core-selected references mechanically; no renderer chooses business importance or values. */
export const resolveTimelineHighlights = (disclosure:TimelineChanges_DISCLOSED):ReadonlyArray<TimelineChange> => {
  if(check_TimelineChanges_DISCLOSED(disclosure,false,[])!==null) { throw new Error("Invalid timeline highlight references."); }
  return disclosure.overview.highlights.map(reference=>resolveTimelineHighlight(disclosure,reference));
};

"""

    let private optionNumber value =
        value |> Option.map string |> Option.defaultValue "null"

    let rec private check =
        function
        | Schema.Ref name -> "check_" + identifier name
        | Schema.Vocabulary(_, schema) -> check schema
        | Schema.EnumPartition(schema, values, selected, rejected, key) ->
            "wireEnumPartition("
            + check schema
            + ",["
            + (values |> List.map quote |> String.concat ",")
            + "],"
            + quote selected
            + ","
            + quote rejected
            + ","
            + quote key
            + ")"
        | Schema.Null -> "wireNull"
        | Schema.Boolean -> "wireBoolean"
        | Schema.Const value -> "wireConstant(" + quote value + ")"
        | Schema.Integer(minimum, maximum) -> $"wireInteger({minimum},{maximum})"
        | Schema.String(pattern, format, minimum, maximum, maximumUtf8Bytes) ->
            stringCheck pattern format minimum maximum maximumUtf8Bytes
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

    and private stringCheck pattern format minimum maximum maximumUtf8Bytes =
        if
            format
            |> Option.exists (fun value ->
                not (List.contains value [ "uuid"; "date"; "date-time" ]))
        then
            invalidOp "The client emitter does not support this string format."

        let pattern = pattern |> Option.map regexLiteral |> Option.defaultValue "null"

        "wireString("
        + optionNumber minimum
        + ","
        + optionNumber maximum
        + ","
        + pattern
        + ","
        + (format |> Option.map quote |> Option.defaultValue "null")
        + ","
        + optionNumber maximumUtf8Bytes
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
                + (if name = "TimelineChanges.DISCLOSED" then
                       "wireTimelineDisclosure(" + check schema + ")"
                   else
                       check schema)
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
