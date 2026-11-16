import { CasePayments } from "./CasePayments";
import type { CurrentCase } from "../../protocol/types";
import { RegionView } from "../../ui/regions/RegionView";
import type { Affordance } from "@contracts/client";

export const CaseFactRegions = ({
  current,
  view,
  onAct,
  onChanged,
}: {
  current: CurrentCase;
  view: CurrentCase["screen"];
  onAct: (action: Affordance) => void;
  onChanged: () => void;
}) => (
  <div className="case-fact-regions">
    {view.regions
      .filter((region) => ["FACTS", "PAYMENTS"].includes(region.role))
      .map((region, index) => {
        if (region.role === "PAYMENTS") {
          return (
            <CasePayments
              key={region.role}
              region={region}
              current={current}
              onAct={onAct}
              onChanged={onChanged}
            />
          );
        }
        return (
          <RegionView
            // oxlint-disable-next-line react/no-array-index-key -- Fact groups are ordered supplied read-only regions.
            key={`${region.role}:${index}`}
            region={region}
            timeZone={current.basis.observation.timeZone}
            onAction={(action) => {
              onAct(action);
            }}
          />
        );
      })}
  </div>
);
