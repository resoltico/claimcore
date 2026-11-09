import type { Region, CaseWorkView } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { RegionView } from "../../ui/regions/RegionView";
export const CaseWork = ({
  value,
  region,
  timeZone,
  onAssign,
  onSummary,
}: {
  value: CaseWorkView;
  region?: Region;
  timeZone: string;
  onAssign?: () => void;
  onSummary?: () => void;
}) => {
  const p = usePresentation();
  return region === undefined ? null : (
    <div className="case-work">
      <RegionView
        region={region}
        timeZone={timeZone}
        headerActions={
          <div className="case-work-controls">
            {value.canAssign && onAssign !== undefined ? (
              <button id="case-work-assignee" type="button" onClick={onAssign}>
                {p.text("ui.changeResponsible")}
              </button>
            ) : null}
            {value.canUpdateSummary && onSummary !== undefined ? (
              <button id="case-work-summary" type="button" onClick={onSummary}>
                {p.text("ui.editSummary")}
              </button>
            ) : null}
          </div>
        }
      />
    </div>
  );
};
