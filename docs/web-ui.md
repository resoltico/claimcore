# Web UI

The [workspace components](../web/src/ui/) own the interface. Page places the heading and primary
action before the operation feedback, view tabs, toolbar and content. Section keeps its heading,
count, secondary actions and body inside one card. The outcome banner takes focus and links to the
receipt after a change.

Field owns control labels, hints, rules and errors. Text, date, select and combobox controls share
the sizing and border tokens in [field.css](../web/src/ui/field.css). MoneyField joins the amount and
currency, with one written hint and error region. Toolbar groups search, filters and exports. On
phones, the search stays in the toolbar and Filters opens a bottom sheet.

Core supplies chip tones, table prominence, column joins, recommendations, consequences and empty
wording. Lists select wide, medium and card layouts from the available container width. The
[layout styles](../web/src/ui/layout.css) declare the table thresholds and the value measures for
identifiers, text, actors, money and chips. Wide rows give each primary or secondary value its
column, with joined values in the target column. Medium rows stack recorded secondary values under
the primary cell. Cards put the reference and primary values first, followed by secondary values
and row controls.

Text uses the workspace font. Identifiers use the mono font at the surrounding size and wrap first
at separators. Amounts break between digit groups or before the currency. A word can split when it
exceeds the width available to it. The browser matrix exercises maximum accepted values in the
[limits fixture](../web/e2e/design-matrix-fixtures.ts) at each layout boundary. Row controls stay
inside the list at every supported width.

A row recommendation uses a short core label on a secondary button. Each page allows one solid
primary outside dialogs.

The cascade runs through reset, tokens, base, layout, components and views. The theme generator reads
[tokens.json](../web/src/theme/tokens.json). Keep authored styles in the declared layer and use logical
properties for inline and block spacing. Check keyboard focus, Arabic, both colour schemes, reduced
motion and forced colours.

L5 confines form controls to the UI components. L6 protects word wrapping. The browser matrix checks
control geometry, word integrity, primary emphasis, page offsets, minimum type size, disclosure
placement, page titles, first-row position and feedback focus. See [Verification](contributing.md#verification).
