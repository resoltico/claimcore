import { useId } from "react";
import type { PresentedMoneyMeter } from "@contracts/client";
import { SuppliedMessage } from "../SuppliedMessage";
import "./meter.css";

/** The native range adapts supplied money; completion and all financial calculations stay in Core. */
export const MoneyMeter = ({
  meter,
  timeZone,
  label,
}: {
  meter: PresentedMoneyMeter;
  timeZone: string;
  label: string;
}) => {
  const caption = useId();
  const target = Number(meter.target.amount);
  const maximum = target === 0 ? 1 : target;
  const value = meter.complete ? maximum : Number(meter.current.amount);
  return (
    <div className="money-meter">
      <meter
        className="progress-meter"
        min={0}
        max={maximum}
        value={value}
        aria-label={label}
        aria-describedby={caption}
      />
      <p id={caption}>
        <SuppliedMessage message={meter.description} timeZone={timeZone} />
      </p>
    </div>
  );
};
