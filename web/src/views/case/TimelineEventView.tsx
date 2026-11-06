import type { TimelineEvent } from "@contracts/client";
import type { ReactNode } from "react";
import { ActorIdentity } from "../../components/ActorIdentity";
import { CharacterWarning } from "../../components/BusinessValue";
import { actorName } from "../../presentation/actorName";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey, renderIsolatedValues } from "../../presentation/messages";

const AuthoredAction = ({ value }: { value: string }) => (
  <>
    <bdi>{value}</bdi>
    <CharacterWarning value={value} />
  </>
);
export const TimelineEventView = ({ event }: { event: TimelineEvent }) => {
  const p = usePresentation();
  const sentence = (
    values: Readonly<Record<string, string>>,
    references: Readonly<Record<string, ReactNode>>,
  ) => renderIsolatedValues(p, event.messageId, values, references);
  if ("person" in event) {
    return sentence(
      { person: actorName(p, event.person) },
      { person: <ActorIdentity key="person" actor={event.person} /> },
    );
  }
  if ("completedAction" in event) {
    return sentence(
      {
        completedAction: event.completedAction,
        nextAction: event.nextAction,
        nextDue: p.date(event.nextDue),
      },
      {
        completedAction: <AuthoredAction key="completedAction" value={event.completedAction} />,
        nextAction: <AuthoredAction key="nextAction" value={event.nextAction} />,
        nextDue: (
          <bdi key="nextDue" dir="ltr">
            <time dateTime={event.nextDue}>{p.date(event.nextDue)}</time>
          </bdi>
        ),
      },
    );
  }
  if ("action" in event) {
    if ("due" in event) {
      return sentence(
        { action: event.action, due: p.date(event.due) },
        {
          action: <AuthoredAction key="action" value={event.action} />,
          due: (
            <bdi key="due" dir="ltr">
              <time dateTime={event.due}>{p.date(event.due)}</time>
            </bdi>
          ),
        },
      );
    }
    return sentence(
      { action: event.action },
      { action: <AuthoredAction key="action" value={event.action} /> },
    );
  }
  return renderCatalogKey(p, event.messageId);
};
