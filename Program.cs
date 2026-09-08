using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

internal static class Program
{
    // ---------------------------------------------------------------------
    // Portal configuration. Base URL includes the "/uind" context path.
    // ---------------------------------------------------------------------
    // Identity is tied to the portal, not to its role in the copy, so the
    // credentials keep matching the right site if the direction is flipped.
    private static readonly Portal DatumProject = new(
        "portal.datumproject.net",
        "https://portal.datumproject.net/uind",
        "datumproject",
        "PRS_DATUM");

    private static readonly Portal Utopia = new(
        "portal.utopiaindustries.pk",
        "https://portal.utopiaindustries.pk/uind",
        "utopiaindustries",
        "PRS_UTOPIA");

    // Direction of the copy: rights are read from Reference, written to Target.
    // Swap these two lines to reverse it.
    private static readonly Portal Reference = Utopia;
    private static readonly Portal Target = DatumProject;

    // Kept outside bin/ so logins and audit history survive a rebuild/clean.
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortalRightsSync");

    // Chrome profile folder: keeps you logged in between runs.
    private static readonly string ProfileDir = Path.Combine(DataDir, "browser-profile");

    private static readonly string AuditDir = Path.Combine(DataDir, "audits");

    // Optional. Deliberately outside the project folder so secrets are never
    // sitting next to the source. Environment variables take precedence.
    private static readonly string CredentialsFile = Path.Combine(DataDir, "credentials.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--dialog-test"))
        {
            return DialogSelfTest();
        }

        return RunAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Renders every popup with sample data so the layouts can be checked
    /// without touching a portal.
    /// </summary>
    private static int DialogSelfTest()
    {
        var sample = new SyncPlan();

        sample.Changes.Add(new Change(
            "positionRights.adminAccess", "Admin Access", "select", "YES", "NO", 1));

        sample.Changes.Add(new Change(
            "positionRights.adminSetupView", "Admin Setup", "select", "ALL", "NO", 1));

        sample.Changes.Add(new Change(
            "positionRightsVms.visitView", "Visit View", "select", "NO", "YES", 1));

        sample.NotEditable.Add("positionRights.isDeveloper (isDeveloper): target has 'true', source has 'false'");
        sample.MissingInTarget.Add("positionRightsUim.garmentsDashboardView (Garments Dashboard - View) = NO");
        sample.ExtraInTarget.Add("positionRightsSurveillance.firCreate (FIR - Create) = NO");

        var report = BuildReport(sample);
        var lockouts = FindLockoutRisks(sample);

        Console.WriteLine(report);
        Console.WriteLine($"lockout risks detected: {string.Join(", ", lockouts)}");

        var review = Dialogs.ReviewChanges(
            $"{sample.Changes.Count} right(s) will be changed on the target",
            report,
            lockouts);

        Console.WriteLine($"approved={review.Approved} protectAdmin={review.ProtectAdminRights}");

        if (review.Approved)
        {
            Console.WriteLine($"confirmSave={Dialogs.ConfirmSave(sample.Changes.Count)}");
        }

        return 0;
    }

    private static async Task<int> RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("Portal Position-Rights Sync");
        Console.WriteLine("===========================");
        Console.WriteLine($"Source : {Reference.BaseUrl}/admin/position-rights");
        Console.WriteLine($"Target : {Target.BaseUrl}/admin/position-rights");

        // Both IDs are collected in one popup, before the browser opens, so the
        // direction of the copy is visible in a single glance.
        var ids = Dialogs.AskEmployeeIds($"{Reference.BaseUrl}/", $"{Target.BaseUrl}/");

        if (ids is null)
        {
            Console.WriteLine("Cancelled. Nothing was done.");
            return 1;
        }

        var sourceInput = ids.Value.Source;
        var targetInput = ids.Value.Target;

        using var playwright = await Playwright.CreateAsync();

        Directory.CreateDirectory(ProfileDir);

        // One persistent browser profile, one tab per portal.
        // Cookies are host-scoped, so the two portal sessions stay independent,
        // and logins survive between runs.
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
            ProfileDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                ViewportSize = ViewportSize.NoViewport,
                Args = new[] { "--start-maximized" }
            });

        context.SetDefaultTimeout(60_000);
        context.SetDefaultNavigationTimeout(60_000);

        var referencePage = context.Pages.Count > 0
            ? context.Pages[0]
            : await context.NewPageAsync();

        var targetPage = await context.NewPageAsync();

        try
        {
            // -------------------------------------------------------------
            // SOURCE PORTAL
            // -------------------------------------------------------------
            Console.WriteLine($"\n--- SOURCE: {Reference.Name} ---");
            await referencePage.BringToFrontAsync();
            await EnsureLoggedInAsync(referencePage, Reference);
            var referenceForm = await OpenRightsFormAsync(referencePage, Reference, sourceInput);

            Console.WriteLine($"  Page    : {referenceForm.Heading}");
            Console.WriteLine($"  URL     : {referencePage.Url}");
            Console.WriteLine($"  Rights  : {referenceForm.Fields.Count} editable fields");

            // -------------------------------------------------------------
            // TARGET PORTAL
            // -------------------------------------------------------------
            Console.WriteLine($"\n--- TARGET: {Target.Name} ---");
            await targetPage.BringToFrontAsync();
            await EnsureLoggedInAsync(targetPage, Target);
            var targetForm = await OpenRightsFormAsync(targetPage, Target, targetInput);

            Console.WriteLine($"  Page    : {targetForm.Heading}");
            Console.WriteLine($"  URL     : {targetPage.Url}");
            Console.WriteLine($"  Rights  : {targetForm.Fields.Count} editable fields");

            // -------------------------------------------------------------
            // CONFIRM WE ARE ON THE RIGHT TWO PEOPLE
            // -------------------------------------------------------------
            Console.WriteLine("\n====================================");
            Console.WriteLine("ABOUT TO COPY RIGHTS");
            Console.WriteLine("====================================");
            Console.WriteLine($"FROM : {referenceForm.Heading}");
            Console.WriteLine($"       {referencePage.Url}");
            Console.WriteLine($"TO   : {targetForm.Heading}");
            Console.WriteLine($"       {targetPage.Url}");

            if (!Dialogs.ConfirmRecords(
                    referenceForm.Heading, referencePage.Url,
                    targetForm.Heading, targetPage.Url))
            {
                Console.WriteLine("Cancelled. Nothing was changed.");
                return 1;
            }

            // -------------------------------------------------------------
            // COMPARE
            // -------------------------------------------------------------
            var plan = BuildPlan(referenceForm, targetForm);
            var report = BuildReport(plan);

            Console.WriteLine(report);

            if (plan.Changes.Count == 0)
            {
                Console.WriteLine("\nNo changes required - the target already matches the source.");

                await WriteAuditAsync(referenceForm, targetForm, plan, targetForm,
                    "No changes required", sourceInput, targetInput,
                    referencePage.Url, targetPage.Url);

                Dialogs.Info(
                    "No changes required.\r\n\r\n" +
                    "The target already matches the source on every editable right.");

                return 0;
            }

            // -------------------------------------------------------------
            // APPROVAL BEFORE EDITING
            // -------------------------------------------------------------
            var lockoutFields = FindLockoutRisks(plan);

            var review = Dialogs.ReviewChanges(
                $"{plan.Changes.Count} right(s) will be changed on the target",
                report,
                lockoutFields);

            if (!review.Approved)
            {
                Console.WriteLine("Cancelled. Nothing was changed.");

                await WriteAuditAsync(referenceForm, targetForm, plan, targetForm,
                    "Cancelled before applying changes", sourceInput, targetInput,
                    referencePage.Url, targetPage.Url);

                return 1;
            }

            if (review.ProtectAdminRights)
            {
                var held = plan.Changes.RemoveAll(c => lockoutFields.Contains(c.Name));

                Console.WriteLine(
                    $"\nHolding back {held} portal-administration field(s) at the operator's request:");

                foreach (var name in lockoutFields)
                {
                    Console.WriteLine($"  - {name}");
                }
            }

            await targetPage.BringToFrontAsync();
            var failed = await ApplyChangesAsync(targetPage, plan.Changes);

            if (failed.Count > 0)
            {
                Console.WriteLine("\nSome fields could not be set on screen. Save is blocked.");

                foreach (var item in failed)
                {
                    Console.WriteLine($"  - {item}");
                }

                var partial = await ReadFormAsync(targetPage);

                await WriteAuditAsync(referenceForm, targetForm, plan, partial,
                    "Blocked - some fields could not be set", sourceInput, targetInput,
                    referencePage.Url, targetPage.Url);

                Dialogs.Error(
                    "Save was blocked.\r\n\r\n" +
                    "These fields could not be set on screen:\r\n\r\n" +
                    string.Join("\r\n", failed.Take(15)) +
                    (failed.Count > 15 ? $"\r\n... and {failed.Count - 15} more" : string.Empty) +
                    "\r\n\r\nNothing was submitted.");

                return 1;
            }

            Console.WriteLine("\nAll changes are set on screen but NOT yet submitted.");
            Console.WriteLine("Review the target portal window before saving.");

            // -------------------------------------------------------------
            // SECOND APPROVAL - SAVE
            // -------------------------------------------------------------
            if (!Dialogs.ConfirmSave(plan.Changes.Count))
            {
                Console.WriteLine("Save cancelled. Nothing was submitted.");

                await WriteAuditAsync(referenceForm, targetForm, plan, targetForm,
                    "Cancelled before save", sourceInput, targetInput,
                    referencePage.Url, targetPage.Url);

                return 1;
            }

            var notice = await SubmitAsync(targetPage);

            if (!string.IsNullOrWhiteSpace(notice))
            {
                Console.WriteLine($"\nPortal message: {notice}");
            }

            // -------------------------------------------------------------
            // VERIFY
            // -------------------------------------------------------------
            var finalForm = await ReadFormAsync(targetPage);
            var verification = BuildPlan(referenceForm, finalForm);

            string status;

            if (verification.Changes.Count == 0)
            {
                var skipped = verification.MissingInTarget.Count
                              + verification.NotEditable.Count
                              + verification.UnsupportedValues.Count;

                status = skipped == 0
                    ? "Successfully matched"
                    : $"Matched on every editable shared field ({skipped} right(s) could not be copied)";

                Console.WriteLine($"\n{status.ToUpperInvariant()}");
            }
            else
            {
                status = "Partially matched - manual review required";

                Console.WriteLine("\nPARTIALLY MATCHED - these fields still differ after save:");

                foreach (var change in verification.Changes)
                {
                    Console.WriteLine(
                        $"  - {change.Name}: target='{change.CurrentValue}' expected='{change.NewValue}'");
                }
            }

            await WriteAuditAsync(referenceForm, targetForm, plan, finalForm,
                status, sourceInput, targetInput, referencePage.Url, targetPage.Url);

            var closing =
                $"{status}.\r\n\r\n" +
                $"Applied {plan.Changes.Count} change(s) to:\r\n{targetForm.Heading}\r\n\r\n" +
                $"{targetPage.Url}";

            if (verification.Changes.Count == 0)
            {
                Dialogs.Info(closing);
            }
            else
            {
                Dialogs.Warn(
                    closing +
                    $"\r\n\r\n{verification.Changes.Count} field(s) still differ. " +
                    "See the console output and the audit file.");
            }

            return verification.Changes.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("\nRUN FAILED");
            Console.WriteLine(ex.Message);
            Dialogs.Error(ex.Message);
            return 1;
        }
    }

    // =====================================================================
    // NAVIGATION
    // =====================================================================

    private static async Task EnsureLoggedInAsync(IPage page, Portal portal)
    {
        var credential = LoadCredential(portal);
        var autoLoginTried = false;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await page.GotoAsync(
                $"{portal.BaseUrl}/admin/position-rights",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            if (!page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  Session active on {portal.BaseUrl}");
                return;
            }

            if (page.Url.Contains("password-change", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{portal.Name}: the portal is forcing a password change. " +
                    "Complete it in the browser, then run this tool again.");
            }

            if (credential != null && !autoLoginTried)
            {
                autoLoginTried = true;

                Console.WriteLine($"  Signing in as '{credential.Username}'...");

                if (await TryAutoLoginAsync(page, credential))
                {
                    continue;
                }

                Console.WriteLine("  Automatic sign-in did not complete.");
            }

            Console.WriteLine($"  Login required: {page.Url}");

            Dialogs.WaitForBrowserStep(
                $"Please sign in to:\r\n\r\n{portal.BaseUrl}/\r\n\r\n" +
                "Use the browser window that just opened, then click OK here.");
        }

        throw new InvalidOperationException(
            $"{portal.Name}: still redirected to the login page. Aborting.");
    }

    /// <summary>
    /// Fills and submits the portal login form. Returns false when the portal
    /// needs something only a human can supply (an OTP code, or a corrected
    /// password), leaving the browser on that screen for manual completion.
    /// </summary>
    private static async Task<bool> TryAutoLoginAsync(IPage page, Credential credential)
    {
        try
        {
            await page.WaitForSelectorAsync(
                "form input[name='username']",
                new PageWaitForSelectorOptions { Timeout = 20_000 });

            // The page's own window.onload fills the device-hash field. Letting it
            // run first keeps this browser profile recognised, which is what avoids
            // an OTP challenge on later runs.
            try
            {
                await page.WaitForFunctionAsync(
                    @"() => {
                        const el = document.querySelector('input[name=""device-hash""]');
                        return !!el && el.value.length > 0;
                    }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                // Older builds may not use a device hash; continue regardless.
            }

            await page.FillAsync("form input[name='username']", credential.Username);
            await page.FillAsync("form input[name='password']", credential.Password);

            var rememberMe = page.Locator("form input[name='remember-me']");

            if (await rememberMe.CountAsync() > 0)
            {
                await rememberMe.First.CheckAsync();
            }

            await page.ClickAsync("form button[type='submit']");

            await page.WaitForLoadStateAsync(
                LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = 60_000 });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Sign-in attempt failed: {ex.Message.Split('\n')[0]}");
            return false;
        }

        if (page.Url.Contains("authenticate", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "  This portal is asking for a one-time code. " +
                "Enter it in the browser window.");

            Dialogs.WaitForBrowserStep(
                "This portal is asking for a one-time code.\r\n\r\n" +
                "Enter it in the browser window, then click OK here.");

            return true;
        }

        if (page.Url.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  The portal rejected those credentials.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Credentials come from environment variables first, then an optional JSON
    /// file kept outside the project folder. Neither is required - without them
    /// the tool simply waits for a manual login.
    /// </summary>
    private static Credential? LoadCredential(Portal portal)
    {
        var user = Environment.GetEnvironmentVariable($"{portal.EnvPrefix}_USERNAME");
        var pass = Environment.GetEnvironmentVariable($"{portal.EnvPrefix}_PASSWORD");

        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            return new Credential(user, pass);
        }

        if (!File.Exists(CredentialsFile))
        {
            return null;
        }

        try
        {
            var store = JsonSerializer.Deserialize<Dictionary<string, Credential>>(
                File.ReadAllText(CredentialsFile), JsonOpts);

            if (store != null &&
                store.TryGetValue(portal.Key, out var credential) &&
                !string.IsNullOrWhiteSpace(credential.Username) &&
                !string.IsNullOrWhiteSpace(credential.Password))
            {
                return credential;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not read {CredentialsFile}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Resolves the typed employee ID to a position-rights edit page and reads it.
    /// </summary>
    private static async Task<RightsForm> OpenRightsFormAsync(
        IPage page,
        Portal portal,
        string input)
    {
        input = input.Trim();

        if (input.StartsWith("#", StringComparison.Ordinal))
        {
            var positionId = input[1..].Trim();

            Console.WriteLine($"  Opening position-rights edit/{positionId} directly...");

            await page.GotoAsync(
                $"{portal.BaseUrl}/admin/position-rights/edit/{positionId}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        else
        {
            var employee = await ResolveEmployeeAsync(page, portal, input);

            Console.WriteLine(
                $"  Employee: {employee.FullName} " +
                $"(serial {employee.SerialNumber}, internal id {employee.Id})");

            await OpenByEmployeeAsync(page, portal, employee.Id);
        }

        if (!page.Url.Contains("/position-rights/edit/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{portal.Name}: expected a position-rights edit page but landed on {page.Url}. " +
                "The employee may have no position assigned, or your account may lack Admin Setup rights.");
        }

        return await ReadFormAsync(page);
    }

    private static async Task<EmployeeMatch> ResolveEmployeeAsync(
        IPage page,
        Portal portal,
        string term)
    {
        var url = $"{portal.BaseUrl}/rest/hrms/employees/search" +
                  $"?term={Uri.EscapeDataString(term)}&limit=25";

        var raw = await page.EvaluateAsync<string>(@"async (url) => {
            const response = await fetch(url, {
                credentials: 'same-origin',
                headers: { 'Accept': 'application/json' }
            });
            const body = await response.text();
            if (!response.ok) {
                return JSON.stringify({ error: response.status + ' ' + response.statusText });
            }
            try {
                const data = JSON.parse(body);
                return JSON.stringify({
                    items: data.map(e => ({
                        id: e.id,
                        serialNumber: e.serialNumber,
                        fullName: e.fullName,
                        positionId: e.positionId,
                        positionTitle: e.positionTitle,
                        isActive: e.isActive
                    }))
                });
            } catch (err) {
                return JSON.stringify({
                    error: 'employee search did not return JSON (the session may have expired)'
                });
            }
        }", url);

        var result = JsonSerializer.Deserialize<EmployeeSearchResult>(raw, JsonOpts)
                     ?? throw new InvalidOperationException("Could not read the employee search response.");

        if (!string.IsNullOrEmpty(result.Error))
        {
            throw new InvalidOperationException(
                $"{portal.Name}: employee search failed - {result.Error}");
        }

        var items = result.Items ?? new List<EmployeeMatch>();

        // Prefer an exact serial-number match over fuzzy name hits.
        var exact = items
            .Where(e => string.Equals(e.SerialNumber?.Trim(), term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = exact.Count > 0 ? exact : items;

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"{portal.Name}: no employee found for '{term}'.");
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        Console.WriteLine($"  {candidates.Count} employees matched '{term}'.");

        var options = candidates
            .Select(c => $"{c.FullName}  (serial {c.SerialNumber})  {c.PositionTitle}")
            .ToList();

        var selected = Dialogs.AskChoice(
            portal.Name,
            $"{candidates.Count} employees matched \"{term}\" on {portal.BaseUrl}/.\r\n" +
            "Select the right person:",
            options);

        if (selected < 0)
        {
            throw new OperationCanceledException(
                $"{portal.Name}: employee selection was cancelled.");
        }

        return candidates[selected];
    }

    /// <summary>
    /// Uses the portal's own /edit-by-employee endpoint, which redirects to the
    /// position-rights edit page for that employee's position.
    /// </summary>
    private static async Task OpenByEmployeeAsync(IPage page, Portal portal, long employeeId)
    {
        var action = $"{portal.BaseUrl}/admin/position-rights/edit-by-employee";

        try
        {
            await page.EvaluateAsync(@"(args) => {
                const form = document.createElement('form');
                form.method = 'POST';
                form.action = args.action;
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = 'employee-id';
                input.value = args.employeeId;
                form.appendChild(input);
                document.body.appendChild(form);
                form.submit();
            }", new { action, employeeId = employeeId.ToString() });
        }
        catch (PlaywrightException)
        {
            // The submit tears down the execution context; the navigation wait
            // below is the real signal of success.
        }

        await page.WaitForURLAsync(
            "**/position-rights/edit/**",
            new PageWaitForURLOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
    }

    // =====================================================================
    // READING THE FORM
    // =====================================================================

    /// <summary>
    /// Reads every editable right in one browser round-trip. Selects and
    /// checkboxes are both rights; hidden previous* mirrors, the Thymeleaf "_"
    /// checkbox markers, and identity fields are excluded.
    /// </summary>
    private static async Task<RightsForm> ReadFormAsync(IPage page)
    {
        await page.WaitForSelectorAsync(
            "form select[name^='positionRights']",
            new PageWaitForSelectorOptions { Timeout = 60_000 });

        var raw = await page.EvaluateAsync<string>(@"() => {
            const skip = (name) => !name
                || name.startsWith('previousPositionRight')
                || name.startsWith('prevPositionRights')
                || name.startsWith('_')
                || name === 'id'
                || name === 'employeeId'
                || name === 'title'
                || name === 'positionRights.id';

            let form = null;
            for (const candidate of document.querySelectorAll('form')) {
                if (candidate.querySelector('select[name^=""positionRights""]')) {
                    form = candidate;
                    break;
                }
            }
            if (!form) {
                return JSON.stringify({ error: 'position rights form not found on this page' });
            }

            const labelOf = (el) => {
                const group = el.closest('.form-group, .form-check');
                let label = group ? group.querySelector('label') : null;
                if (!label && el.id) {
                    label = form.querySelector('label[for=""' + CSS.escape(el.id) + '""]');
                }
                return label ? label.textContent.trim().replace(/\s+/g, ' ') : '';
            };

            const groups = new Map();

            for (const el of form.querySelectorAll('select[name], input[type=""checkbox""][name]')) {
                const name = el.getAttribute('name');
                if (skip(name)) continue;

                const isSelect = el.tagName === 'SELECT';
                const value = isSelect ? el.value : (el.checked ? 'true' : 'false');
                const options = isSelect
                    ? Array.from(el.options).map(o => o.value)
                    : ['true', 'false'];

                let group = groups.get(name);
                if (!group) {
                    group = {
                        name: name,
                        kind: isSelect ? 'select' : 'checkbox',
                        label: labelOf(el),
                        values: [],
                        options: []
                    };
                    groups.set(name, group);
                }

                group.values.push(value);
                for (const option of options) {
                    if (!group.options.includes(option)) group.options.push(option);
                }
            }

            // Rights the portal renders as a plain hidden input instead of a
            // control: the logged-in account is not allowed to edit them.
            // Their value still round-trips on save, so they are preserved.
            const readOnly = [];
            for (const el of form.querySelectorAll('input[type=""hidden""][name]')) {
                const name = el.getAttribute('name');
                if (skip(name) || groups.has(name)) continue;
                readOnly.push({ name: name, value: el.value });
            }

            const fields = [];
            for (const group of groups.values()) {
                fields.push({
                    name: group.name,
                    kind: group.kind,
                    label: group.label,
                    value: group.values[0],
                    values: group.values,
                    options: group.options,
                    occurrences: group.values.length,
                    consistent: group.values.every(v => v === group.values[0])
                });
            }

            const container = form.closest('.col-sm');
            const heading = container ? container.querySelector('h3') : null;

            return JSON.stringify({
                heading: heading ? heading.textContent.trim().replace(/\s+/g, ' ') : '(unknown)',
                url: location.href,
                fields: fields,
                readOnly: readOnly
            });
        }");

        var form = JsonSerializer.Deserialize<RightsForm>(raw, JsonOpts)
                   ?? throw new InvalidOperationException("Could not read the rights form.");

        if (!string.IsNullOrEmpty(form.Error))
        {
            throw new InvalidOperationException(form.Error);
        }

        // This form legitimately renders a handful of field names twice.
        // Report any whose duplicates disagree instead of aborting the run.
        var inconsistent = form.Fields.Where(f => !f.Consistent).ToList();

        if (inconsistent.Count > 0)
        {
            Console.WriteLine(
                $"  Note: {inconsistent.Count} field name(s) appear more than once " +
                "with differing values on this page:");

            foreach (var field in inconsistent)
            {
                Console.WriteLine($"    - {field.Name}: [{string.Join(", ", field.Values)}]");
            }
        }

        return form;
    }

    // =====================================================================
    // COMPARE
    // =====================================================================

    private static SyncPlan BuildPlan(RightsForm reference, RightsForm target)
    {
        var plan = new SyncPlan();
        var targetByName = target.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        var targetReadOnly = target.ReadOnly
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        foreach (var source in reference.Fields)
        {
            if (!targetByName.TryGetValue(source.Name, out var match))
            {
                // The portal renders a right the account may not edit as a hidden
                // input rather than omitting it, so tell those two cases apart.
                if (targetReadOnly.TryGetValue(source.Name, out var lockedValue))
                {
                    if (!string.Equals(lockedValue, source.Value, StringComparison.Ordinal))
                    {
                        plan.NotEditable.Add(
                            $"{source.Name} ({Describe(source)}): target has '{lockedValue}', " +
                            $"source has '{source.Value}'");
                    }

                    continue;
                }

                plan.MissingInTarget.Add($"{source.Name} ({Describe(source)}) = {source.Value}");
                continue;
            }

            // Already equal, and no duplicate occurrence disagrees.
            if (string.Equals(source.Value, match.Value, StringComparison.Ordinal) && match.Consistent)
            {
                continue;
            }

            if (match.Kind == "select" &&
                !match.Options.Contains(source.Value, StringComparer.Ordinal))
            {
                plan.UnsupportedValues.Add(
                    $"{source.Name} ({Describe(source)}): source value '{source.Value}' " +
                    $"is not offered on the target (available: {string.Join(", ", match.Options)})");

                continue;
            }

            plan.Changes.Add(new Change(
                source.Name,
                Describe(source),
                match.Kind,
                match.Value,
                source.Value,
                match.Occurrences));
        }

        var referenceNames = reference.Fields
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);

        // A right the SOURCE account may not edit is still rendered as a hidden
        // input carrying its real value, so it can be copied whenever the target
        // side is editable. Without this, a non-admin source login would silently
        // under-report the rights it is meant to be replicating.
        foreach (var locked in reference.ReadOnly)
        {
            if (!referenceNames.Add(locked.Name))
            {
                continue;
            }

            if (!targetByName.TryGetValue(locked.Name, out var match))
            {
                continue;
            }

            if (string.Equals(locked.Value, match.Value, StringComparison.Ordinal) && match.Consistent)
            {
                continue;
            }

            if (match.Kind == "select" &&
                !match.Options.Contains(locked.Value, StringComparer.Ordinal))
            {
                plan.UnsupportedValues.Add(
                    $"{locked.Name}: source value '{locked.Value}' is not offered on the " +
                    $"target (available: {string.Join(", ", match.Options)})");

                continue;
            }

            plan.Changes.Add(new Change(
                locked.Name,
                $"{Describe(match)}  [read-only on the source]",
                match.Kind,
                match.Value,
                locked.Value,
                match.Occurrences));
        }

        foreach (var extra in target.Fields.Where(f => !referenceNames.Contains(f.Name)))
        {
            plan.ExtraInTarget.Add($"{extra.Name} ({Describe(extra)}) = {extra.Value}");
        }

        return plan;
    }

    private static string Describe(RightsField field) =>
        string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;

    /// <summary>
    /// One report, shown both in the console and in the review popup.
    /// </summary>
    private static string BuildReport(SyncPlan plan)
    {
        var report = new StringBuilder();

        report.AppendLine("====================================");
        report.AppendLine("COMPARISON");
        report.AppendLine("====================================");

        if (plan.Changes.Count == 0)
        {
            report.AppendLine("No differing fields.");
        }
        else
        {
            report.AppendLine($"{plan.Changes.Count} field(s) will be changed on the target:");
            report.AppendLine();

            foreach (var change in plan.Changes.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var occurrence = change.Occurrences > 1
                    ? $"  [appears {change.Occurrences}x on the page]"
                    : string.Empty;

                report.AppendLine($"  {change.Name}");
                report.AppendLine($"      {change.Label}");
                report.AppendLine($"      '{change.CurrentValue}'  ->  '{change.NewValue}'{occurrence}");
            }
        }

        AppendSection(report, plan.UnsupportedValues,
            "field(s) CANNOT be copied (the target dropdown offers no such option):");

        if (plan.NotEditable.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(
                $"{plan.NotEditable.Count} right(s) differ but YOUR TARGET ACCOUNT MAY NOT EDIT them.");
            report.AppendLine(
                "  The target portal renders these read-only (their current values are kept on save).");
            report.AppendLine(
                "  To copy them, sign in to the target portal as an ADMIN or HR HOD account:");

            foreach (var item in plan.NotEditable)
            {
                report.AppendLine($"  - {item}");
            }
        }

        AppendSection(report, plan.MissingInTarget,
            "source right(s) DO NOT EXIST on the target portal and will be skipped:");

        AppendSection(report, plan.ExtraInTarget,
            "right(s) exist only on the target portal and will be left untouched:");

        return report.ToString();
    }

    private static void AppendSection(StringBuilder report, List<string> items, string caption)
    {
        if (items.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine($"{items.Count} {caption}");

        foreach (var item in items)
        {
            report.AppendLine($"  - {item}");
        }
    }

    /// <summary>
    /// Flags changes that would switch off the rights granting access to the
    /// position-rights screen itself. Copying these onto the position you
    /// administer the portal with locks you out, and the portal offers no way
    /// to undo that yourself.
    /// </summary>
    private static List<string> FindLockoutRisks(SyncPlan plan)
    {
        // PositionUtils builds ROLE_ADMIN_ACCESS_<adminAccess> and
        // ROLE_ADMIN_SETUP_VIEW_<adminSetupView>; AuthorizationMenuRoles.ADMIN_SETUP
        // then requires ROLE_ADMIN_ACCESS_YES or ROLE_ADMIN_SETUP_VIEW_ALL.
        var grantingValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["positionRights.adminAccess"] = "YES",
            ["positionRights.adminSetupView"] = "ALL"
        };

        return plan.Changes
            .Where(c => grantingValues.TryGetValue(c.Name, out var granting)
                        && string.Equals(c.CurrentValue, granting, StringComparison.Ordinal)
                        && !string.Equals(c.NewValue, granting, StringComparison.Ordinal))
            .Select(c => c.Name)
            .ToList();
    }

    // =====================================================================
    // APPLY
    // =====================================================================

    private static async Task<List<string>> ApplyChangesAsync(IPage page, List<Change> changes)
    {
        var failed = new List<string>();
        var applied = 0;

        foreach (var change in changes)
        {
            var selector = change.Kind == "select"
                ? $"form select[name={CssQuote(change.Name)}]"
                : $"form input[type='checkbox'][name={CssQuote(change.Name)}]";

            var locator = page.Locator(selector);
            var count = await locator.CountAsync();

            if (count == 0)
            {
                failed.Add($"{change.Name}: element disappeared from the page");
                continue;
            }

            try
            {
                // A name can legitimately appear more than once on this form;
                // every occurrence is set to the same value.
                for (var i = 0; i < count; i++)
                {
                    var element = locator.Nth(i);

                    await element.ScrollIntoViewIfNeededAsync();

                    if (change.Kind == "select")
                    {
                        await element.SelectOptionAsync(
                            new SelectOptionValue { Value = change.NewValue },
                            new LocatorSelectOptionOptions { Timeout = 15_000 });
                    }
                    else
                    {
                        await element.SetCheckedAsync(
                            change.NewValue == "true",
                            new LocatorSetCheckedOptions { Timeout = 15_000 });
                    }
                }

                applied++;

                Console.WriteLine(
                    $"  set {change.Name}: '{change.CurrentValue}' -> '{change.NewValue}'");
            }
            catch (Exception ex)
            {
                failed.Add($"{change.Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        Console.WriteLine($"\nApplied {applied} of {changes.Count} change(s) on screen.");

        return failed;
    }

    private static async Task<string> SubmitAsync(IPage page)
    {
        var saveButton = page.Locator("form button[type='submit'], form input[type='submit']");

        if (await saveButton.CountAsync() == 0)
        {
            throw new InvalidOperationException("Save button was not found on the target page.");
        }

        await saveButton.First.ScrollIntoViewIfNeededAsync();
        await saveButton.First.ClickAsync();

        // The controller redirects back to /position-rights/edit/{id}.
        try
        {
            await page.WaitForURLAsync(
                "**/position-rights/edit/**",
                new PageWaitForURLOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });
        }
        catch (TimeoutException)
        {
            // Fall through to verification rather than assuming failure.
        }

        var alert = page.Locator(".alert").First;

        return await alert.CountAsync() > 0
            ? (await alert.InnerTextAsync()).Trim()
            : string.Empty;
    }

    // =====================================================================
    // AUDIT
    // =====================================================================

    private static async Task WriteAuditAsync(
        RightsForm reference,
        RightsForm originalTarget,
        SyncPlan plan,
        RightsForm finalTarget,
        string status,
        string sourceInput,
        string targetInput,
        string sourceUrl,
        string targetUrl)
    {
        var audit = new
        {
            Timestamp = DateTimeOffset.Now,
            Status = status,
            Source = new
            {
                Portal = Reference.BaseUrl,
                EmployeeInput = sourceInput,
                Url = sourceUrl,
                Heading = reference.Heading,
                Rights = ToMap(reference)
            },
            Target = new
            {
                Portal = Target.BaseUrl,
                EmployeeInput = targetInput,
                Url = targetUrl,
                Heading = originalTarget.Heading,
                RightsBefore = ToMap(originalTarget),
                RightsAfter = ToMap(finalTarget)
            },
            Plan = new
            {
                plan.Changes,
                plan.UnsupportedValues,
                plan.NotEditable,
                plan.MissingInTarget,
                plan.ExtraInTarget
            }
        };

        Directory.CreateDirectory(AuditDir);

        var file = Path.Combine(
            AuditDir,
            $"rights_audit_{Sanitize(sourceInput)}_to_{Sanitize(targetInput)}_" +
            $"{DateTime.Now:yyyyMMdd_HHmmss}.json");

        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(audit, JsonOpts));

        Console.WriteLine($"\nAudit written to: {file}");
    }

    private static Dictionary<string, string> ToMap(RightsForm form) =>
        form.Fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());

        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    // =====================================================================
    // HELPERS
    // =====================================================================

    private static string CssQuote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

        return $"\"{escaped}\"";
    }

    // =====================================================================
    // MODELS
    // =====================================================================

    private sealed record Portal(string Name, string BaseUrl, string Key, string EnvPrefix);

    private sealed record Credential(string Username, string Password);

    private sealed record Change(
        string Name,
        string Label,
        string Kind,
        string CurrentValue,
        string NewValue,
        int Occurrences);

    private sealed class SyncPlan
    {
        public List<Change> Changes { get; } = new();
        public List<string> UnsupportedValues { get; } = new();
        public List<string> NotEditable { get; } = new();
        public List<string> MissingInTarget { get; } = new();
        public List<string> ExtraInTarget { get; } = new();
    }

    private sealed class RightsForm
    {
        public string Heading { get; set; } = "(unknown)";
        public string Url { get; set; } = string.Empty;
        public string? Error { get; set; }
        public List<RightsField> Fields { get; set; } = new();
        public List<ReadOnlyField> ReadOnly { get; set; } = new();
    }

    private sealed class ReadOnlyField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class RightsField
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = "select";
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public List<string> Values { get; set; } = new();
        public List<string> Options { get; set; } = new();
        public int Occurrences { get; set; }
        public bool Consistent { get; set; } = true;
    }

    private sealed class EmployeeSearchResult
    {
        public string? Error { get; set; }
        public List<EmployeeMatch>? Items { get; set; }
    }

    private sealed class EmployeeMatch
    {
        public long Id { get; set; }
        public string? SerialNumber { get; set; }
        public string? FullName { get; set; }
        public long? PositionId { get; set; }
        public string? PositionTitle { get; set; }
        public bool? IsActive { get; set; }
    }
}
