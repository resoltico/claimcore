import type { ActorReviewItem } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
import { worklistPath } from "../routing/worklistQuery";
import { ActorIdentity } from "./ActorIdentity";
import { ActorRoleScopes } from "./ActorRoleScopes";
import { CopyValue } from "./CopyValue";

export const ActorSummary = ({
  actor,
  identityInHeader = false,
}: {
  actor: ActorReviewItem;
  identityInHeader?: boolean;
}) => {
  const p = usePresentation();
  const responsible = actor.responsibleCases;
  const href =
    responsible === undefined
      ? null
      : worklistPath({ view: responsible.view, responsibleActor: responsible.responsibleActor });
  return (
    <>
      <dl className="actor-summary">
        {identityInHeader ? null : (
          <>
            <dt>{p.text("ui.people.person")}</dt>
            <dd>
              <ActorIdentity actor={actor.actor} />
            </dd>
          </>
        )}
        {actor.contact === null ? null : (
          <>
            <dt>{p.fieldLabel("profile.contact")}</dt>
            <dd>
              <bdi>{actor.contact}</bdi>
            </dd>
          </>
        )}
        <dt>{p.text("ui.people.status")}</dt>
        <dd>{p.term("accountState", actor.state)}</dd>
        <dt>{p.text("ui.people.roles")}</dt>
        <dd>
          <ActorRoleScopes roles={actor.roles} />
        </dd>
        {responsible === undefined || href === null ? null : (
          <>
            <dt>{p.text("ui.people.responsibleFor")}</dt>
            <dd>
              <a
                href={href}
                onClick={(event) => {
                  followLink(event, () => {
                    navigate(href);
                  });
                }}
              >
                {p.text("ui.summaryCounts", { count: p.integer(responsible.count) })}
              </a>
            </dd>
          </>
        )}
      </dl>
      <details className="actor-record-details">
        <summary>{p.text("label.recordDetails")}</summary>
        <dl>
          <dt>{p.text("ui.account")}</dt>
          <dd>
            <p>{p.term("principalKind", actor.principal.kind)}</p>
            <p>
              {p.fieldLabel("principal.issuer")}: <bdi dir="ltr">{actor.principal.issuer}</bdi>
              <CopyValue
                icon
                label={p.fieldLabel("principal.issuer")}
                value={actor.principal.issuer}
              />
            </p>
            <p>
              {p.fieldLabel("principal.subject")}: <bdi>{actor.principal.subject}</bdi>
              <CopyValue
                icon
                label={p.fieldLabel("principal.subject")}
                value={actor.principal.subject}
              />
            </p>
            <p>
              {p.text("input.actorId.label")}: <bdi dir="ltr">{actor.actor.actorId}</bdi>
              <CopyValue icon label={p.text("input.actorId.label")} value={actor.actor.actorId} />
            </p>
          </dd>
        </dl>
      </details>
    </>
  );
};
