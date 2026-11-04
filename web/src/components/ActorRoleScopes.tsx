import type { ActorReviewItem } from "@contracts/client";
import { usePresentation } from "../presentation/context";

/** Raw assignments from one core snapshot, independent of effective permissions. */
export const ActorRoleScopes = ({ roles }: { roles: ActorReviewItem["roles"] }) => {
  const p = usePresentation();
  return (
    <ul className="actor-role-scopes">
      {roles.map((summary) => (
        <li key={summary.role}>
          <strong>{p.term("role", summary.role)}</strong>
          {summary.installation ? <span>{p.term("scope", "INSTALLATION")}</span> : null}
          <span>{p.text("ui.people.caseGrants", { count: p.integer(summary.caseCount) })}</span>
        </li>
      ))}
    </ul>
  );
};
