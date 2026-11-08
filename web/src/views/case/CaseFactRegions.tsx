import type { Region } from "@contracts/client";
import { termKeys } from "@contracts/messages";
import type { CurrentCase } from "../../protocol/types";
import { operationEntry } from "../../protocol/operationCatalog";
import { usePresentation } from "../../presentation/context";
import { RegionView } from "../../ui/regions/RegionView";
import { Button } from "../../ui/Button";
import type { ActKind } from "../../operation/metadata";

const editAct = (region: Region, current: CurrentCase) => {
  const group = Object.entries(termKeys.group).find(([, title]) => title === region.titleId)?.[0];
  return (["AMEND_REGISTRATION", "DECIDE"] as const)
    .filter((act) => current.advice.acts.includes(act))
    .find((act) => {
      const entry = operationEntry({ subject: "CASE", kind: act });
      return (
        entry.family === "BUSINESS" &&
        entry.affectedSections.length === 1 &&
        entry.affectedSections[0] === group
      );
    });
};
export const CaseFactRegions = ({
  current,
  view,
  onAct,
}: {
  current: CurrentCase;
  view: CurrentCase["view"];
  onAct: (act: ActKind) => void;
}) => {
  const p = usePresentation();
  return (
    <div className="case-fact-regions">
      {view.regions
        .filter((region) => ["FACTS"].includes(region.role))
        .map((region, index) => {
          const act = region.role === "FACTS" ? editAct(region, current) : undefined;
          return (
            <RegionView
              // oxlint-disable-next-line react/no-array-index-key -- Fact groups are ordered supplied read-only regions.
              key={`${region.role}:${index}`}
              region={region}
              timeZone={current.work.basis.timeZone}
              {...(act === undefined
                ? {}
                : {
                    headerActions: (
                      <Button
                        variant="secondary"
                        onPress={() => {
                          onAct(act);
                        }}
                      >
                        {p.actLabel(act)}
                      </Button>
                    ),
                  })}
            />
          );
        })}
    </div>
  );
};
