import { DateEntryDraft } from "./DateEntryDraft";
import { I18nProvider } from "react-aria-components/I18nProvider";
import { FieldHelp } from "./FieldHelp";
import { parseDate } from "@internationalized/date";
import { DateField, DateInput, DateSegment } from "react-aria-components/DateField";
import { Calendar, CalendarGrid, CalendarCell } from "react-aria-components/Calendar";
import { IconButton } from "../ui/IconButton";
import { DialogTrigger, Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { Popover } from "react-aria-components/ComboBox";
import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import type { FieldCatalogEntry } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import { usePresentation } from "../presentation/context";
const dateValue = (value: string) => {
  try {
    return parseDate(value);
  } catch {
    return null;
  }
};
export type TypedFieldProps = {
  field: FieldCatalogEntry;
  value: string;
  inputName: string;
  disabled: boolean;
  error?: Notice | undefined;
  onChange: (value: string) => void;
};
const CalendarPopup = ({ field, value, disabled, onChange }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <DialogTrigger>
      <IconButton
        icon="calendar"
        isDisabled={disabled}
        label={`${p.text("ui.calendar")}: ${p.fieldLabel(field.name)}`}
      />
      <Popover className="calendar-popover">
        <Dialog aria-label={p.fieldLabel(field.name)}>
          <I18nProvider locale={p.displayLocale}>
            <Calendar
              aria-label={p.fieldLabel(field.name)}
              value={dateValue(value)}
              onChange={(next) => {
                onChange(next.toString());
              }}
            >
              <header>
                <IconButton
                  slot="previous"
                  icon="chevron-start"
                  label={p.text("ui.previousMonth")}
                />
                <Heading lang={p.displayLocale} />
                <IconButton slot="next" icon="chevron-end" label={p.text("ui.nextMonth")} />
              </header>
              <CalendarGrid lang={p.displayLocale}>
                {(day) => <CalendarCell date={day} />}
              </CalendarGrid>
            </Calendar>
          </I18nProvider>
        </Dialog>
      </Popover>
    </DialogTrigger>
  );
};
const DateEntry = ({ field, value, inputName, disabled, error, onChange }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <DateField
      id={`field-${inputName}`}
      name={inputName}
      className="text-field"
      data-field-name={field.name}
      value={dateValue(value)}
      onChange={(next) => {
        onChange(next?.toString() ?? "");
      }}
      isDisabled={disabled}
      isInvalid={error !== undefined}
      validationBehavior="aria"
    >
      <DateEntryDraft path={inputName} canonical={value} disabled={disabled} />
      <div className="field-label-row">
        <Label>{p.fieldLabel(field.name)}</Label>
        <FieldHelp label={p.fieldLabel(field.name)} ruleId={field.ruleId} />
      </div>
      <DateInput lang={p.displayLocale} aria-invalid={error !== undefined || undefined}>
        {(segment) => <DateSegment segment={segment} />}
      </DateInput>
      <Text slot="description" className="field-description">
        <small>{p.hint(field)}</small>
      </Text>
      {error === undefined ? null : <FieldError>{p.notice(error)}</FieldError>}
    </DateField>
  );
};
export const CatalogDate = (props: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <div className="date-control">
      <I18nProvider locale={p.displayLocale}>
        <DateEntry {...props} />
      </I18nProvider>
      <CalendarPopup {...props} />
    </div>
  );
};
