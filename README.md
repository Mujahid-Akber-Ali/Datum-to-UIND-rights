# PortalRightsSync

C# / .NET 8 (WinForms + Playwright) automation that copies a person's **position rights** from one
UIND portal to the same person on another UIND portal.

- Source (read from): `https://portal.utopiaindustries.pk/uind`
- Target (written to): `https://portal.datumproject.net/uind`

The same person usually has a **different employee ID on each portal**, so the tool
asks for both IDs and resolves each one to the right position-rights page itself.

## Setup

```powershell
dotnet restore
dotnet build
pwsh bin/Debug/net8.0-windows/playwright.ps1 install chromium
```

The Playwright browsers only need installing once per machine.

## Sign-in

The tool signs in for you if it can find credentials, and otherwise just waits while
you log in by hand. Either way the session is remembered in its own Chrome profile,
so normal runs skip the login entirely.

Credentials are read from environment variables first:

```powershell
$env:PRS_UTOPIA_USERNAME = "mujahid.ali"
$env:PRS_UTOPIA_PASSWORD = "..."
$env:PRS_DATUM_USERNAME  = "admin"
$env:PRS_DATUM_PASSWORD  = "..."
```

If those are unset, it falls back to `%LOCALAPPDATA%\PortalRightsSync\credentials.json`:

```json
{
  "utopiaindustries": { "username": "mujahid.ali", "password": "..." },
  "datumproject":     { "username": "admin",       "password": "..." }
}
```

That file holds plaintext passwords. It lives outside the project folder on purpose so
it can never be committed with the source. Delete it to go back to manual login.

If the portal asks for a one-time code, the tool pauses and lets you type it into the
browser window.

### Account privileges affect what can be copied

The portal renders rights the signed-in account may not edit as hidden inputs rather
than controls. That cuts both ways, and the tool handles each side differently:

- **On the source**, a hidden input still carries the right's real value, so it is
  read and copied normally. The change is labelled *[read-only on the source]*. A
  non-admin source login therefore does not silently under-report what it replicates.
- **On the target**, a right the account may not edit genuinely cannot be written.
  Those are reported under *MAY NOT EDIT* instead of being skipped quietly, and their
  existing values are preserved on save — nothing is wiped.

So the limiting factor is the **target** login. To be able to write the HOD / salary /
payroll / management flags, sign in to the target portal as **ADMIN** or **HR HOD**.

## Run

```powershell
dotnet run
```

The whole flow is driven by popups; the console window behind them just carries the
detailed log.

1. **Popup 1 — Employee IDs.** One popup with two boxes: the *COPY FROM* ID on
   `portal.utopiaindustries.pk`, and the *COPY TO* ID on `portal.datumproject.net`,
   which is the portal that gets modified.
2. Chrome opens with one tab per portal and signs in. If a portal needs a manual
   login or a one-time code, a popup tells you and waits.
3. **Popup 2 — Confirm records.** Shows both names and both URLs so you can check you
   have the right two people before anything is compared.
4. **Popup 3 — Review changes.** The full comparison in a scrollable list, with
   *Apply changes* / *Cancel*.
5. **Popup 4 — Confirm save.** The changes are set in the browser but not submitted;
   review the window, then save.
6. **Popup 5 — Result**, plus a JSON audit file.

Nothing is written until popup 3, and nothing is submitted until popup 4.

To bypass the employee lookup and open a rights page by its own ID, type `#` and the
ID — for example `#8579` opens `/admin/position-rights/edit/8579`.

The login is remembered between runs, so normal runs go straight from popup 1 to
popup 2.

### Self-lockout guard

The two rights that control access to the position-rights screen itself are
`positionRights.adminAccess` and `positionRights.adminSetupView`. If copying the
source would switch either of them off, popup 3 shows a warning and a **pre-ticked**
checkbox that holds those two fields back.

Leave it ticked when the target is the position you administer the portal with:
turning those off logs you out of the screen, and the portal gives you no way to undo
it yourself. Untick it for a true field-for-field copy.

To preview the popups without touching a portal:

```powershell
dotnet run -- --dialog-test
```

## What it does

1. Opens both portals in one Chrome profile (separate tabs, independent sessions).
2. Resolves each employee ID via the portal's own
   `/rest/hrms/employees/search` endpoint, then its own
   `/admin/position-rights/edit-by-employee` endpoint — the same path the portal UI uses.
3. Shows both employee names and asks you to confirm they are the right two people.
4. Reads every editable right on both pages — **dropdowns and checkboxes** — in a
   single browser call, and compares them by form field name.
5. Splits the comparison into five groups (see below).
6. Shows the full comparison and requires **Apply changes** before it touches the target page.
7. Sets each differing field on screen, leaving the page unsaved for you to review.
8. Requires a second confirmation before submitting.
9. Re-reads the target page after saving and reports whether it now matches.
10. Writes a JSON audit file.

## Reading the comparison

The two portals do not run identical builds, so the report separates:

| Group | Meaning |
|---|---|
| **will be changed** | Present and editable on both sides with different values — these get copied. |
| **CANNOT be copied** | The target dropdown has no such option (e.g. source is `ALL`, target only offers `YES`/`NO`). Skipped. |
| **MAY NOT EDIT** | The right exists on the target but your target login is not allowed to change it. Sign in as ADMIN / HR HOD to copy these. Existing values are preserved. |
| **DO NOT EXIST on the target** | A right the source portal has and the target build does not. Skipped. |
| **exist only on the target** | A right the target has and the source does not. Left untouched. |

The last four of those are reported but never block the run — you decide whether the
copyable subset is good enough before choosing **Apply changes**.

## Safety behaviour

- Nothing is written until you choose **Apply changes**, and nothing is submitted
  until you confirm the save. They are two separate popups.
- Identity fields (`id`, `employeeId`, `positionRights.id`, `title`) and the hidden
  `previousPositionRight*` mirror inputs are never touched.
- If any field cannot be set on screen, the save is blocked entirely.
- A source value that the target dropdown does not offer is skipped and reported
  rather than forced.
- This form renders about 15 field names twice; every occurrence of a name is set to
  the same value, and any pre-existing disagreement between duplicates is reported.

## Files it writes

Both live outside `bin/`, so they survive a rebuild:

- Chrome profile: `%LOCALAPPDATA%\PortalRightsSync\browser-profile`
- Audit JSON: `%LOCALAPPDATA%\PortalRightsSync\audits\rights_audit_<src>_to_<tgt>_<timestamp>.json`

The audit records both employees, both URLs, the full before/after rights map for the
target, and everything that was skipped. The full path is printed at the end of a run.

To force a fresh login on both portals, delete the `browser-profile` folder.

## Requirements

- .NET 8 SDK or newer, with the Windows Desktop workload (WinForms popups)
- An account with **Admin Setup** rights on both portals

## Reversing the direction

The copy runs **from** `Reference` **to** `Target`, set in `Program.cs`:

```csharp
private static readonly Portal Reference = Utopia;        // read from
private static readonly Portal Target    = DatumProject;  // written to
```

Swap those two values to flip it. Credentials are keyed by portal
(`utopiaindustries` / `datumproject`), not by role, so they keep matching the right
site when the direction changes.
