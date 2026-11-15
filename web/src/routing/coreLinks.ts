import type { Link } from "@contracts/client";
import { casePath, referencePath } from "./routes";
import { worklistPath } from "./worklistQuery";
import { remindersPath } from "./reminderQuery";
const caseTarget = (target: Extract<Link, { endpoint: "case.get" }>["input"]["target"]) =>
  target.kind === "ID" ? casePath(target.caseId) : referencePath(target.caseReference);
const peopleLinkPath = (
  link: Extract<
    Link,
    { endpoint: "people.grants" | "people.history" | "people.list" | "people.requests" }
  >,
): string => {
  switch (link.endpoint) {
    case "people.grants":
      return `/people/${encodeURIComponent(link.input.actorId)}`;
    case "people.history":
      return link.input.actorId === undefined
        ? "/people-history"
        : `/people-history?actorId=${encodeURIComponent(link.input.actorId)}`;
    default:
      return "/people";
  }
};
/** Core chooses the query; the web interface chooses its corresponding local route. */
export const coreLinkPath = (link: Link): string | undefined => {
  switch (link.endpoint) {
    case "case.get":
      return caseTarget(link.input.target);
    case "reminder.get":
      return `${caseTarget(link.input.target)}/reminders/${encodeURIComponent(link.input.reminderId)}`;
    case "people.grants":
    case "people.list":
    case "people.requests":
    case "people.history":
      return peopleLinkPath(link);
    case "operation.get":
      return `/recovery/${encodeURIComponent(link.input.operationId ?? "")}`;
    case "case.list":
      return worklistPath(link.input);
    case "reminder.list":
      return link.input.target === undefined ? remindersPath(link.input) : undefined;
    default:
      // Context, view discovery and case subqueries are embedded reads without standalone routes.
      return undefined;
  }
};
