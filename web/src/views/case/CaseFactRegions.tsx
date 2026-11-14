import { CasePayments } from "./CasePayments";
import type { CurrentCase } from "../../protocol/types";
import { usePresentation } from "../../presentation/context";
import { RegionView } from "../../ui/regions/RegionView";
import { Button } from "../../ui/Button";
import type { ActKind } from "../../operation/metadata";

export const CaseFactRegions = ({
  current,
  view,
  onAct,
  onChanged,
}: {
  current: CurrentCase;
  view: CurrentCase["screen"];
  onAct: (act: ActKind, paymentId?: string) => void;
  onChanged: () => void;
}) => {
  const p = usePresentation();
  return (
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
          const act = current.actions.some(
            (action) => action.task === "case.amendRegistration" && action.blocked === null,
          )
            ? "case.amendRegistration"
            : undefined;
          return (
            <RegionView
              // oxlint-disable-next-line react/no-array-index-key -- Fact groups are ordered supplied read-only regions.
              key={`${region.role}:${index}`}
              region={region}
              timeZone={current.basis.observation.timeZone}
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
