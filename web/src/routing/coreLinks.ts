import { isCaseListSelection, type CaseSelector, type Link } from "@contracts/client";
import { isObject } from "../protocol/client";
import { isTerm } from "../presentation/terms";
import { casePath, referencePath } from "./routes";
import { worklistPath } from "./worklistQuery";
import { parseReminderQuery, reminderPagePath } from "./reminderQuery";
const targetInput = (link: Link): CaseSelector | undefined => {
  const { target } = link.input;
  if (!isObject(target)) {
    return undefined;
  }
  if (target["kind"] === "ID" && typeof target["caseId"] === "string") {
    return { kind: "ID", caseId: target["caseId"] };
  }
  if (target["kind"] === "REFERENCE" && typeof target["caseReference"] === "string") {
    return { kind: "REFERENCE", caseReference: target["caseReference"] };
  }
  return undefined;
};
export const caseTargetPath = (target: CaseSelector) =>
  target.kind === "ID" ? casePath(target.caseId) : referencePath(target.caseReference);
export const caseLinkTarget = (link: Link) =>
  link.endpoint === "case.get" ? targetInput(link) : undefined;
export const caseLinkId = (link: Link) => {
  const target = caseLinkTarget(link);
  return target?.kind === "ID" ? target.caseId : undefined;
};
export const personLinkActorId = (link: Link) =>
  link.endpoint === "people.grants" && typeof link.input["actorId"] === "string"
    ? link.input["actorId"]
    : undefined;
export const reminderLinkTarget = (link: Link) => {
  const target = targetInput(link);
  const { reminderId } = link.input;
  return link.endpoint === "reminder.get" && target !== undefined && typeof reminderId === "string"
    ? { target, reminderId }
    : undefined;
};
export const caseLinkView = (link: Link) => {
  const { view } = link.input;
  return link.endpoint === "case.list" && typeof view === "string" && isTerm("caseView", view)
    ? view
    : undefined;
};
export const reminderLinkView = (link: Link) => {
  const { view } = link.input;
  return link.endpoint === "reminder.list" &&
    typeof view === "string" &&
    isTerm("reminderView", view)
    ? view
    : undefined;
};
const filterQuery = (input: Link["input"]) => {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(input)) {
    const name = key === "text" ? "q" : key;
    for (const item of Array.isArray(value) ? value : [value]) {
      if (typeof item === "string" || typeof item === "number" || typeof item === "boolean") {
        params.append(name, String(item));
      }
    }
  }
  return params.toString();
};
/** Core chooses the endpoint and input; this table adapts them to local routes. */
export const attachmentFragment = "case-attachments";
const routes: Partial<Record<Link["endpoint"], (link: Link) => string | undefined>> = {
  "case.get": (link) => {
    const target = targetInput(link);
    return target === undefined ? undefined : caseTargetPath(target);
  },
  "case.attachments": (link) => {
    const target = targetInput(link);
    return target === undefined ? undefined : `${caseTargetPath(target)}#${attachmentFragment}`;
  },
  "reminder.get": (link) => {
    const selected = reminderLinkTarget(link);
    return selected === undefined
      ? undefined
      : `${caseTargetPath(selected.target)}/reminders/${encodeURIComponent(selected.reminderId)}`;
  },
  "people.grants": (link) => {
    const actor = personLinkActorId(link);
    return actor === undefined ? undefined : `/people/${encodeURIComponent(actor)}`;
  },
  "people.history": (link) => {
    const actor = link.input["actorId"];
    return typeof actor === "string"
      ? `/people-history?actorId=${encodeURIComponent(actor)}`
      : "/people-history";
  },
  "people.list": () => "/people",
  "people.requests": () => "/people",
  "operation.get": (link) => {
    const operation = link.input["operationId"];
    return typeof operation === "string"
      ? `/recovery/${encodeURIComponent(operation)}`
      : "/recovery/";
  },
  "case.list": ({ input }) => {
    const { cursor: _cursor, limit: _limit, ...selection } = input;
    return isCaseListSelection(selection) ? worklistPath(selection) : undefined;
  },
  "reminder.list": (link) => {
    const target = targetInput(link);
    const { target: _target, cursor: _cursor, limit: _limit, ...filters } = link.input;
    const query = parseReminderQuery(filterQuery(filters));
    return reminderPagePath(
      { ...query, ...(target === undefined ? {} : { target }) },
      query.view === undefined,
    );
  },
};
export const coreLinkPath = (link: Link): string | undefined => routes[link.endpoint]?.(link);
