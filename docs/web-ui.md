# Web UI

The [workspace components](../web/src/ui/) own the interface. Page places the heading and primary
action before the operation feedback, view tabs, toolbar and content. Section keeps its heading,
count, secondary actions and body inside one card. The outcome banner takes focus and links to the
receipt after a change.

Field owns control labels, hints, rules and errors. Text, date, select and combobox controls share
the sizing and border tokens in [field.css](../web/src/ui/field.css). MoneyField joins the amount and
currency, with one written hint and error region. Toolbar groups search, filters and exports. On
phones, the search stays in the toolbar and Filters opens a bottom sheet.

Core supplies chip tones, table prominence, recommendations, consequences and empty wording.
DataTable shows primary and secondary columns in a wide container. Secondary values stack under a
primary cell in medium containers. Narrow rows put the reference and primary values first, followed
by recorded secondary values and the recommended action. Chips stay intact. Identifiers may wrap
within a word in narrow rows and dialogs. A row recommendation uses a short core label on a secondary
button. Each page allows one solid primary outside dialogs.

The cascade runs through reset, tokens, base, layout, components and views. The theme generator reads
[tokens.json](../web/src/theme/tokens.json). Keep authored styles in the declared layer and use logical
properties for inline and block spacing. Check keyboard focus, Arabic, both colour schemes, reduced
motion and forced colours.

L5 confines form controls to the UI components. L6 protects word wrapping. The browser matrix checks
control geometry, word integrity, primary emphasis, page offsets, minimum type size, disclosure
placement, page titles, first-row position and feedback focus. See [Verification](contributing.md#verification).
