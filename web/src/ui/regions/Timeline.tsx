import type { RegionContent, Affordance } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { PresentedValue } from "../../components/PresentedValue";
import { SectionsView } from "../SectionsView";
import { coreLinkPath } from "../../routing/coreLinks";
import { navigate } from "../../routing/navigation";
import { Actions } from "./Actions";

import type { RowLink } from "./Table";
import {} from "./timeline.css";
import { SuppliedMessage } from "../SuppliedMessage";
export const Timeline = ({
  content,
  timeZone,
  onAction,
}: {
  content: Extract<RegionContent, { kind: "TIMELINE" }>;
  timeZone: string;
  onAction?: (action: Affordance, originId?: string) => void;
}) => {
  const p = usePresentation();
  return (
    <ol className="region-timeline">
      {content.entries.map((entry) => {
        const path = entry.link === undefined ? undefined : coreLinkPath(entry.link);
        const values = [...entry.highlights, ...entry.details].flatMap(
          (section) => section.entries,
        );
        const destination =
          values.find((value) => value.value.kind === "REFERENCE") ??
          values.find((value) => value.prominence === "PRIMARY");
        const link: RowLink | undefined =
          path === undefined
            ? undefined
            : {
                href: path,
                ...(destination === undefined ? {} : { column: destination.key }),
                follow: () => {
                  navigate(path);
                },
              };
        return (
          <li key={entry.key}>
            <h3>
              <SuppliedMessage message={entry.summary} timeZone={timeZone} />
            </h3>
            <SectionsView
              sections={entry.highlights}
              headingLevel={4}
              timeZone={timeZone}
              {...(link === undefined ? {} : { link })}
            />
            <p>
              <PresentedValue value={{ kind: "ACTOR", value: entry.who }} timeZone={timeZone} /> ·{" "}
              <PresentedValue value={{ kind: "INSTANT", value: entry.when }} timeZone={timeZone} />
            </p>
            {entry.details.length === 0 ? null : (
              <details>
                <summary>{p.text("label.recordDetails")}</summary>
                <SectionsView
                  sections={entry.details}
                  headingLevel={4}
                  timeZone={timeZone}
                  {...(link === undefined ? {} : { link })}
                />
              </details>
            )}
            <Actions
              actions={entry.actions ?? []}
              {...(onAction === undefined ? {} : { onAction })}
              compact
            />
          </li>
        );
      })}
    </ol>
  );
};
