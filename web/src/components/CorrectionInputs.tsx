import { Entries } from "../ui/regions/Entries";
import { renderCatalogKey } from "../presentation/messages";
import { usePresentation } from "../presentation/context";
import type { CurrentCase } from "../api/service";
import type { ReactNode } from "react";
export const CorrectionInputs = ({
  group,
  current,
  children,
}: {
  group: string | null;
  current: CurrentCase | null;
  children: ReactNode;
}) => {
  const p = usePresentation();
  if (group === null) {
    return children;
  }
  const section = current?.screen.currentGroups?.find((item) => item.group === group)?.section;
  const title = section === undefined ? null : renderCatalogKey(p, section.titleId);
  return (
    <div className={section === undefined ? undefined : "correction-replacement"}>
      {section !== undefined && current !== null && title !== null ? (
        <aside className="correction-current">
          <h3>{p.text("ui.before", { value: title })}</h3>
          <Entries entries={section.entries} timeZone={current.basis.observation.timeZone} />
        </aside>
      ) : null}
      <div>
        {title === null ? null : <h3>{p.text("ui.after", { value: title })}</h3>}
        {children}
      </div>
    </div>
  );
};
