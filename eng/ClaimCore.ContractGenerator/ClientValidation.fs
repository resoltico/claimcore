namespace ClaimCore.ContractGeneration

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

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
const hasHumanDocumentShape = (value:unknown,structural:WireCheck,draft:boolean,path:WireIssue):value is HumanDocument => structural(value,draft,path)===null;
const humanActions = (screen:Screen):string[] => screen.regions.flatMap(region => {
  if(region.content.kind!=="ACTIONS") { return []; }
  return [...(region.content.recommended===null?[]:[region.content.recommended]),...region.content.groups.flatMap(group=>group.acts)];
});
type CaseInteractionStructure = Extract<InteractionContext,{readonly kind:"CASE"}>;
const humanWorkSeedValid = (seed:WorkInputSeed,editable:ReadonlySet<string>):boolean =>
  (seed.summary===null||editable.has(humanSummaryInput))&&(seed.responsible===null||editable.has(humanResponsibleInput));
const humanEditableBindings = (context:CaseInteractionStructure):boolean => {
  const offered=new Set<string>(context.tasks);
  const declared=new Set<string>(taskDefinitions.filter(task=>offered.has(task.id)).flatMap(task=>task.inputs.map(input=>input.id)));
  return new Set(context.tasks).size===context.tasks.length&&new Set(context.editableInputs).size===context.editableInputs.length&&context.editableInputs.every(input=>declared.has(input));
};
const humanSeedBindings = (document:HumanDocument,context:CaseInteractionStructure):boolean => {
  const {seeds}=context;
  if(document.disclosure!=="CURRENT"&&(seeds.registration!==null||seeds.decision!==null||seeds.work!==null)) { return false; }
  const editable=new Set<string>(context.editableInputs);
  if(seeds.registration!==null&&!humanRegistrationInputs.every(input=>editable.has(input))) { return false; }
  if(seeds.decision!==null&&!humanDecisionInputs.every(input=>editable.has(input))) { return false; }
  return seeds.work===null||humanWorkSeedValid(seeds.work,editable);
};
const humanActionBindings = (context:CaseInteractionStructure,actions:ReadonlyArray<string>):boolean => {
  const tasks=new Set<string>(context.tasks);
  if(tasks.has(humanCorrectionTask)!==(context.correction!==null)) { return false; }
  return new Set(actions).size===actions.length&&actions.every(act=>{const task=humanTaskByAct[act];return task!==undefined&&tasks.has(task);});
};
const humanPageActionBindings = (offered:ReadonlyArray<string>,actions:ReadonlyArray<string>):boolean => {
  const tasks=new Set(offered);
  return tasks.size===offered.length&&new Set(actions).size===actions.length&&actions.every(act=>{const task=humanTaskByAct[act];return task!==undefined&&tasks.has(task);});
};
const humanReminderBindings = (document:HumanDocument,context:Extract<InteractionContext,{readonly kind:"REMINDER"}>):boolean => {
  if(document.disclosure!=="CURRENT"&&(context.actionSeed!==null||context.dueDateSeed!==null)) { return false; }
  const offered=new Set<string>(context.tasks);
  const declared=new Set<string>(taskDefinitions.filter(task=>offered.has(task.id)).flatMap(task=>task.inputs.map(input=>input.id)));
  return (context.actionSeed===null||declared.has(humanReminderActionInput))&&(context.dueDateSeed===null||declared.has(humanReminderDueInput));
};
const humanReviewBindings = (value:HumanDocument,context:Extract<InteractionContext,{readonly kind:"REVIEW"}>,actions:ReadonlyArray<string>):boolean => {
  if(actions.length!==0) { return false; }
  const expected=humanReviewTasks[context.task];
  if(expected===undefined||value.disclosure!=="CURRENT"||value.basis.kind!=="SNAPSHOT") { return false; }
  const inputs=new Set(context.inputs);
  return inputs.size===context.inputs.length&&inputs.size===expected.inputs.length&&expected.inputs.every(input=>inputs.has(input))&&context.target.kind===expected.target&&context.consequence===expected.consequence&&context.confirmation.kind===expected.confirmation;
};
const humanReceiptBindings = (value:HumanDocument,context:Extract<InteractionContext,{readonly kind:"RECEIPT"}>,actions:ReadonlyArray<string>):boolean => {
  if(actions.length!==0) { return false; }
  if(value.basis.kind!=="SNAPSHOT") { return false; }
  if(context.refreshCase!==null&&(context.target.kind==="AUTHORITY"||context.refreshCase!==context.target.caseId)) { return false; }
  if(context.target.kind!=="REMINDER") { return true; }
  const ids=context.target.versions.map(version=>version.reminderId);
  return new Set(ids).size===ids.length;
};
const humanPaymentBindings = (value:HumanDocument,context:Extract<InteractionContext,{readonly kind:"PAYMENT_PAGE"}>,actions:ReadonlyArray<string>):boolean => {
  if(actions.length!==0) { return false; }
  const ids=context.payments.map(handle=>handle.paymentId);
  const keys=value.screen.regions.filter(region=>region.role==="PAYMENTS").flatMap(region=>region.content.kind==="TABLE"?region.content.rows.map(row=>row.key):[]);
  return new Set(ids).size===ids.length&&ids.length===keys.length&&keys.every(key=>ids.includes(key))&&(value.disclosure==="CURRENT"||ids.length===0);
};
const humanNoteBindings = (value:HumanDocument,context:Extract<InteractionContext,{readonly kind:"CASE"|"NOTE_PAGE"}>):boolean => {
  const ids=context.notes.map(item=>item.noteId);
  const keys=value.screen.regions.filter(region=>region.role==="NOTES").flatMap(region=>region.content.kind==="TABLE"?region.content.rows.map(row=>row.key):[]);
  const remove=humanTaskByAct["REMOVE_NOTE"];
  const add=humanTaskByAct["ADD_NOTE"];
  return new Set(ids).size===ids.length&&ids.length===keys.length&&ids.every((id,index)=>id===keys[index])&&(value.disclosure==="CURRENT"||ids.length===0)&&context.tasks.some(task=>task===remove)===context.notes.some(item=>item.canRemove)&&(context.kind!=="NOTE_PAGE"||context.tasks.every(task=>task===add||task===remove));
};
const humanReminderListBindings = (value:HumanDocument,context:Extract<InteractionContext,{readonly kind:"REMINDERS"}>):boolean =>
  context.reminders.every(item=>{
    const tasks=new Set<string>(item.tasks);
    return tasks.size===item.tasks.length&&item.tasks.every(task=>humanReminderTasks.includes(task))&&
      (item.actionSeed===null||tasks.has(humanTaskByAct["REMINDER_UPDATE"]??""))&&
      (item.dueDateSeed===null||tasks.has(humanTaskByAct["REMINDER_UPDATE"]??"")||tasks.has(humanTaskByAct["REMINDER_REOPEN"]??""))&&
      (value.disclosure==="CURRENT"||tasks.size===0);
  });
const humanCaseBindings = (value:HumanDocument,context:CaseInteractionStructure,actions:ReadonlyArray<string>):boolean => humanEditableBindings(context)&&humanSeedBindings(value,context)&&humanActionBindings(context,actions)&&humanNoteBindings(value,context);
const humanDocumentBindings = (value:HumanDocument):boolean => {
  const actions=humanActions(value.screen);
  const {context}=value;
  const groups=value.screen.currentGroups??[];
  if(groups.length>0 && (context.kind!=="CASE"||value.disclosure!=="CURRENT"||groups.some(item=>!context.correction?.[item.group].includes("UPDATE")))) { return false; }
  switch(context.kind) {
    case "PAYMENT_PAGE": return humanPaymentBindings(value,context,actions);
    case "ASSIGNEES": {
      const ids=context.actorIds;
      const keys=value.screen.regions.filter(region=>region.role==="RESULTS").flatMap(region=>region.content.kind==="TABLE"?region.content.rows.map(row=>row.key):[]);
      return actions.length===0&&new Set(ids).size===ids.length&&ids.length===keys.length&&ids.every((id,index)=>id===keys[index])&&(value.disclosure==="CURRENT"||ids.length===0);
    }
    case "REVIEW": return humanReviewBindings(value,context,actions);
    case "RECEIPT": return humanReceiptBindings(value,context,actions);
    case "REMINDER": return humanReminderBindings(value,context)&&humanPageActionBindings(context.tasks,actions);
    case "CASES": {
      const ids=context.caseIds;
      const keys=value.screen.regions.filter(region=>region.role==="RESULTS").flatMap(region=>region.content.kind==="TABLE"?region.content.rows.map(row=>row.key):[]);
      return new Set(ids).size===ids.length&&ids.length===keys.length&&ids.every((id,index)=>id===keys[index])&&(value.disclosure==="CURRENT"||ids.length===0)&&humanPageActionBindings(context.tasks,actions);
    }
    case "ACCESS_REQUESTS":
    case "PEOPLE":
    case "REMINDERS": {
      const ids=context.kind==="PEOPLE"?context.actorIds:context.kind==="REMINDERS"?context.reminders.map(item=>item.reminderId):context.requests.map(item=>item.key);
      const keys=value.screen.regions.filter(region=>region.role==="RESULTS").flatMap(region=>region.content.kind==="TABLE"?region.content.rows.map(row=>row.key):[]);
      return actions.length===0&&new Set(ids).size===ids.length&&ids.length===keys.length&&ids.every((id,index)=>id===keys[index])&&(value.disclosure==="CURRENT"||ids.length===0)&&(context.kind!=="REMINDERS"||humanReminderListBindings(value,context));
    }
    case "NOTE_PAGE": return humanNoteBindings(value,context)&&humanPageActionBindings(context.tasks,actions);
    case "PERSON": return value.disclosure==="CURRENT"&&context.tasks.every(task=>humanPersonTasks.includes(task))&&(context.profileSeed===null||context.tasks.includes(humanProfileTask))&&humanPageActionBindings(context.tasks,actions);
    case "REQUESTER":
    case "PAGE": return humanPageActionBindings(context.tasks,actions);
    case "CASE": return humanCaseBindings(value,context,actions);
    case "EMPTY":
    case "CONTINUATION": return actions.length===0;
  }
};
const wireHumanDocument = (structural:WireCheck):WireCheck => (value,draft,path) => {
  if(!hasHumanDocumentShape(value,structural,draft,path)) { return path; }
  return humanDocumentBindings(value)?null:path;
};
// Rendering references are structural; these guards never calculate business facts.
const hasScreenShape = (value:unknown,structural:WireCheck,draft:boolean,path:WireIssue):value is Screen => structural(value,draft,path)===null;
const wireScreen = (structural:WireCheck):WireCheck => (value,draft,path) => {
  if(!hasScreenShape(value,structural,draft,path)) { return path; }
  const roles=value.regions.filter(region=>region.role!=="FACTS").map(region=>region.role);
  const groups=(value.currentGroups??[]).map(item=>item.group);
  return new Set(roles).size===roles.length&&new Set(groups).size===groups.length?null:[...path,"regions"];
};
const hasContentShape = (value:unknown,structural:WireCheck,draft:boolean,path:WireIssue):value is RegionContent => structural(value,draft,path)===null;
type TableStructure = Extract<RegionContent,{readonly kind:"TABLE"}>;
const tableRowsValid = (value:TableStructure):boolean => {
  const columns=value.columns.map(column=>column.key);
  const rows=value.rows.map(row=>row.key);
  return new Set(columns).size===columns.length&&new Set(rows).size===rows.length&&value.rows.every(row=>row.values.length===columns.length);
};
const tableGroupsValid = (value:TableStructure):boolean => {
  const groups=value.groups??[];
  if(groups.length===0) { return true; }
  const rows=value.rows.map(row=>row.key);
  const grouped=groups.flatMap(group=>group.rowKeys);
  return new Set(grouped).size===grouped.length&&new Set(rows).size===new Set(grouped).size&&rows.every(key=>grouped.includes(key))&&groups.every(group=>group.count>=group.rowKeys.length);
};
const wireRegionContent = (structural:WireCheck):WireCheck => (value,draft,path) => {
  if(!hasContentShape(value,structural,draft,path)) { return path; }
  if(value.kind!=="TABLE") { return null; }
  return tableRowsValid(value)&&tableGroupsValid(value)?null:path;
};
const hasMessageShape = (value:unknown,structural:WireCheck,draft:boolean,path:WireIssue):value is PresentedMessage => structural(value,draft,path)===null;
const wirePresentedMessage = (structural:WireCheck):WireCheck => (value,draft,path) => {
  if(!hasMessageShape(value,structural,draft,path)) { return path; }
  const keys=value.arguments.map(argument=>argument.key);
  return new Set(keys).size===keys.length?null:[...path,"arguments"];
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
        ClaimCore.Wire.HostFailureReason.all
        |> List.map ClaimCore.Wire.HostFailureReason.status
        |> List.distinct
        |> List.map (fun status ->
            string status
            + ":"
            + check (ClaimCore.Wire.HostFailureCodecs.responseSchema status))
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
                + (match name with
                   | "TimelineChanges.DISCLOSED" -> "wireTimelineDisclosure(" + check schema + ")"
                   | "HumanDocument"
                   | "HumanReviewDocument"
                   | "HumanReceiptDocument" -> "wireHumanDocument(" + check schema + ")"
                   | name when
                       name.StartsWith("HumanReadDocument_", System.StringComparison.Ordinal)
                       ->
                       "wireHumanDocument(" + check schema + ")"
                   | "Screen" -> "wireScreen(" + check schema + ")"
                   | "RegionContent" -> "wireRegionContent(" + check schema + ")"
                   | "PresentedMessage" -> "wirePresentedMessage(" + check schema + ")"
                   | _ -> check schema)
                + ")(value,draft,path); }")

        let inputToken field =
            TaskInputId.Target(InputTarget.Field field) |> TaskInputId.token

        let inputs group =
            BusinessField.ofGroup group
            |> List.map (inputToken >> quote)
            |> String.concat ","

        let byAct =
            ActKind.all
            |> List.filter ((<>) (ActKind.Authority AuthorityKind.ProvisionOwner))
            |> List.map (fun act ->
                quote (ActKind.token act) + ":" + quote (TaskId.key (TaskId.Operation act)))
            |> String.concat ","

        let personTasks =
            AuthorityKind.all
            |> List.filter AuthorityKind.targetsExistingActor
            |> List.map (ActKind.Authority >> TaskId.Operation >> TaskId.key)
            |> JsonSerializer.Serialize

        let reviewTasks =
            TaskId.all
            |> List.choose (function
                | TaskId.Operation act as task ->
                    let declaration = TaskDeclarations.describe task

                    let inputs =
                        (declaration.Inputs @ (declaration.Collections |> List.collect _.Inputs))
                        |> List.map (fun input -> TaskInputId.token input.Input)
                        |> List.distinct

                    Some(
                        TaskId.key task,
                        {|
                            target = ActSubject.token (ActKind.subject act)
                            consequence =
                                Vocabulary.token Consequence.vocabulary (Consequence.ofAct act)
                            confirmation =
                                if ActKind.subject act = ActSubject.Authority then
                                    "OPERATION"
                                else
                                    "CASE_REFERENCE"
                            inputs = inputs
                        |}
                    )
                | TaskId.Query _ -> None)
            |> Map.ofList
            |> JsonSerializer.Serialize

        [
            "const humanReminderTasks: ReadonlyArray<string> = "
            + JsonSerializer.Serialize(
                ReminderActKind.all
                |> List.filter ((<>) ReminderActKind.Create)
                |> List.map (ActKind.Reminder >> TaskId.Operation >> TaskId.key)
            )
            + ";"
            "const humanPersonTasks: ReadonlyArray<string> = " + personTasks + ";"
            "const humanProfileTask = "
            + quote (TaskId.key (TaskId.Operation(ActKind.Authority AuthorityKind.SetProfile)))
            + ";"
            "const humanReviewTasks: Readonly<Partial<Record<string,{readonly target:string;readonly consequence:string;readonly confirmation:string;readonly inputs:ReadonlyArray<TaskInputId>}>>> = "
            + reviewTasks
            + ";"
            "const humanRegistrationInputs = ["
            + inputs RecordGroup.Registration
            + "] as const;"
            "const humanDecisionInputs = [" + inputs RecordGroup.Decision + "] as const;"
            "const humanTaskByAct: Readonly<Partial<Record<string,string>>> = {"
            + byAct
            + "};"
            "const humanCorrectionTask = "
            + quote (TaskId.key (TaskId.Operation(ActKind.Case CaseActKind.Correct)))
            + ";"
            "const humanSummaryInput = "
            + quote (inputToken BusinessField.HandlingSummary)
            + ";"
            "const humanReminderActionInput = "
            + quote (TaskInputId.Target InputTarget.ReminderAction |> TaskInputId.token)
            + ";"
            "const humanReminderDueInput = "
            + quote (TaskInputId.Target InputTarget.ReminderDueDate |> TaskInputId.token)
            + ";"
            "const humanResponsibleInput = "
            + quote (inputToken BusinessField.ResponsibleActor)
            + ";"
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
            "export const isResponseFor = <E extends EndpointId,R extends Representation>(endpoint:E,representation:R,value:unknown):value is ResponseFor<E,R> => isResponse(endpoint,value)&&(!(\"representation\" in value)||value.representation===representation);"
            "export const buildRequest = <E extends EndpointId>(endpoint:E,value:unknown):RequestByEndpoint[E] => { const matches = (candidate:unknown):candidate is RequestByEndpoint[E] => requestChecks[endpoint](candidate,true,[]) === null; if (!matches(value)) { throw new Error(\"Invalid endpoint draft structure.\"); } return value; };"
            "export const isCatalogDocument = (value:unknown):value is CatalogDocument => check_Presentation_CatalogDocument(value,false,[]) === null;"
        ]
        |> String.concat "\n"
