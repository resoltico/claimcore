import type { ActorReviewItem } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
import { worklistPath } from "../routing/worklistQuery";
import { ActorIdentity } from "./ActorIdentity";

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
      {identityInHeader ? null : <ActorIdentity actor={actor.actor} />}
      {responsible === undefined || href === null ? null : (
        <a
          className="actor-detail-link"
          href={href}
          onClick={(event) => {
            followLink(event, () => {
              navigate(href);
            });
          }}
        >
          {p.text("ui.summaryCounts", { count: p.integer(responsible.count) })}
        </a>
      )}
    </>
  );
};
