import type { CaseSelector, ReminderListRequest } from "@contracts/client";
import { isTerm } from "../presentation/terms";
export type ReminderQuery = Omit<ReminderListRequest, "cursor" | "limit">;
const invalidSelection = ():never => { throw new Error("Invalid reminder selection."); };
const selectionParameters = (search:string) => {
  const params = new URLSearchParams(search);
  const allowed = ["view","state","due","assignment","q","includeInactive","caseId","caseReference","filters","cursor","limit"];
  for(const key of params.keys()) {
    if(!allowed.includes(key) || params.getAll(key).length !== 1) { invalidSelection(); }
  }
  const filters=params.get("filters");
  if(filters!==null && filters!=="manual") { invalidSelection(); }
  const inactive=params.get("includeInactive");
  if(inactive!==null && !["true","false"].includes(inactive)) { invalidSelection(); }
  return params;
};
const caseSelection = (params:URLSearchParams):CaseSelector|undefined => {
  const caseId=params.get("caseId");
  const caseReference=params.get("caseReference");
  if(caseId!==null) {
    if(caseReference!==null || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(caseId) || caseId==="00000000-0000-0000-0000-000000000000") { invalidSelection(); }
    return {kind:"ID",caseId};
  }
  if(caseReference===null) { return undefined; }
  if(caseReference.length===0) { invalidSelection(); }
  return {kind:"REFERENCE",caseReference};
};
const queryChoices = (result: ReminderQuery, values:{view:string|null;state:string|null;due:string|null;assignment:string|null}):ReminderQuery => ({
  ...result,
  ...(values.view!==null && isTerm("reminderView",values.view)?{view:values.view}:{}),
  ...(values.state!==null && isTerm("reminderState",values.state)?{state:values.state}:{}),
  ...(values.due!==null && isTerm("reminderDue",values.due)?{due:values.due}:{}),
  ...(values.assignment!==null && isTerm("assignment",values.assignment)?{assignment:values.assignment}:{})
});

export const parseReminderQuery = (search:string):ReminderQuery => {
  const params=selectionParameters(search);
  for(const [key,vocabulary] of [["view","reminderView"],["state","reminderState"],["due","reminderDue"],["assignment","assignment"]] as const) {
    const value=params.get(key);
    if(value!==null && !isTerm(vocabulary,value)) { invalidSelection(); }
  }
  const target=caseSelection(params);
  const view=params.get("view");
  const state=params.get("state");
  const due=params.get("due");
  const assignment=params.get("assignment");
  const text=params.get("q");
  const result: ReminderQuery = {
    ...(target===undefined?{}:{target}),
    ...(text===null || text===""?{}:{text}),
    ...(params.get("includeInactive")==="true"?{includeInactive:true}:{})
  };
  return queryChoices(result, {view,state,due,assignment});
};

export const remindersPath = (query: ReminderQuery): string => {
  const params = new URLSearchParams();
  for (const name of ["view", "state", "due", "assignment"] as const) {
    if (query[name] !== undefined) {
      params.set(name, query[name]);
    }
  }
  if (query.text !== undefined && query.text !== "") {
    params.set("q", query.text);
  }
  if (query.includeInactive === true) {
    params.set("includeInactive", "true");
  }
  if (query.target !== undefined) {
    params.set(
      query.target.kind === "ID" ? "caseId" : "caseReference",
      query.target.kind === "ID" ? query.target.caseId : query.target.caseReference,
    );
  }
  const suffix = params.toString();
  return suffix === "" ? "/reminders" : `/reminders?${suffix}`;
};

/** Manual filtering is interface navigation state, never a Core reminder view or request property. */
export const parseReminderPageQuery = (
  search: string,
): { manual: boolean; query: ReminderQuery } => {
  const manual = new URLSearchParams(search).get("filters") === "manual";
  const parsed = parseReminderQuery(search);
  const { view: _view, ...filters } = parsed;
  return { manual, query: manual ? filters : parsed };
};
export const reminderPagePath = (query: ReminderQuery, manual: boolean): string => {
  if (!manual) {
    return remindersPath(query);
  }
  const { view: _view, ...filters } = query;
  const path = remindersPath(filters);
  return `${path}${path.includes("?") ? "&" : "?"}filters=manual`;
};
