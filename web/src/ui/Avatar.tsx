import type { ActorRef } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { Icon } from "./icons";
import "./avatar.css";
export const Avatar = ({
  actor,
  showInitials = true,
}: {
  actor: ActorRef;
  showInitials?: boolean;
}) => {
  const p = usePresentation();
  const initials =
    showInitials && actor.displayName.kind === "NAMED"
      ? actor.displayName.value
          .split(/\s+/u)
          .slice(0, 2)
          .map(
            (word) =>
              [...new Intl.Segmenter(p.displayLocale, { granularity: "grapheme" }).segment(word)][0]
                ?.segment ?? "",
          )
          .join("")
          .toLocaleUpperCase(p.displayLocale)
      : null;
  return (
    <span className="person-avatar" aria-hidden="true">
      {initials ?? <Icon name="person" />}
    </span>
  );
};
