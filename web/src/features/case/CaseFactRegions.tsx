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
  onAct: (action: Affordance, origin?: string) => void;
  onChanged: () => void;
}) => {
  const display = (region: CurrentCase["screen"]["regions"][number]) => (
    <RegionView
      region={region}
      timeZone={current.basis.observation.timeZone}
      onAction={(action, _row, origin) => {
        onAct(action, origin);
      }}
    />
  );
  const facts = view.regions.filter(({ role }) => role === "FACTS");
  const parties = view.regions.find(({ role }) => role === "PARTIES");
  const payments = view.regions.find(({ role }) => role === "PAYMENTS");
  return (
    <>
      {facts.length === 0 ? null : (
        <div className="case-area-claim">
          {facts.map((region, index) => (
            // oxlint-disable-next-line react/no-array-index-key -- Fact groups are supplied in reading order.
            <div key={index}>{display(region)}</div>
          ))}
        </div>
      )}
      {parties === undefined ? null : <div className="case-area-parties">{display(parties)}</div>}
      {payments === undefined ? null : (
        <div className="case-area-payments">
          <CasePayments region={payments} current={current} onAct={onAct} onChanged={onChanged} />
        </div>
      )}
    </>
  );
};
