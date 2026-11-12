import type { Region } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { RegionView } from "../../ui/regions/RegionView";
export const CaseWork = ({
  canAssign,
  canUpdateSummary,
  region,
  timeZone,
  onAssign,
  onSummary,
}: {
  canAssign: boolean;
  canUpdateSummary: boolean;
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
            {canAssign && onAssign !== undefined ? (
              <button id="case-work-assignee" type="button" onClick={onAssign}>
                {p.text("ui.changeResponsible")}
              </button>
            ) : null}
            {canUpdateSummary && onSummary !== undefined ? (
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
