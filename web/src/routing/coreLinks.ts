import type { Link } from "@contracts/client";
import { casePath, referencePath } from "./routes";
import { worklistPath } from "./worklistQuery";
import { remindersPath } from "./reminderQuery";
const caseTarget = (target: Extract<Link, { endpoint: "case.get" }>["input"]["target"]) =>
  target.kind === "ID" ? casePath(target.caseId) : referencePath(target.caseReference);
/** Core chooses the query; the web interface chooses its corresponding local route. */
export const coreLinkPath = (link: Link): string | undefined => {
  switch (link.endpoint) {
    case "case.get":
    case "reminder.get":
      return caseTarget(link.input.target);
    case "people.grants":
      return `/people/${encodeURIComponent(link.input.actorId)}`;
    case "people.list":
      return "/people";
    case "people.history":
      return link.input.actorId === undefined
        ? "/people-history"
        : `/people-history?actorId=${encodeURIComponent(link.input.actorId)}`;
    case "operation.get":
      return `/recovery/${encodeURIComponent(link.input.operationId ?? "")}`;
    case "case.list":
      return worklistPath(link.input);
    case "reminder.list":
      return link.input.target === undefined ? remindersPath(link.input) : undefined;
    default:
      return undefined;
  }
};
