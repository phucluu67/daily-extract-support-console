# Deployment

Two ways to run this. The first is what it was built for and needs no approvals. The
second is a real project — read the whole section before promising it to anyone.

---

## A. Portable — one exe per person (the intended way)

### Build

```powershell
pwsh -File build.ps1
```

Produces `dist\`:

```
ExtractConsole.exe      single self-contained binary, ~50 MB
appsettings.json        environments
```

Those are the only two files. The HTML/CSS/JS are embedded in the exe;
`appsettings.json` is deliberately left loose so it can be edited without a rebuild.

This builds the **console**, which people run on their own machines. The unattended
checker is not built or deployed by anything — see
[the checker section](#the-checker--nothing-is-deployed-anywhere).

It is self-contained: the target machine needs **no .NET runtime**, no installer and no
admin rights. `PublishTrimmed` is deliberately off — `Microsoft.Data.SqlClient` reflects
over configuration and auth types and breaks when trimmed. `InvariantGlobalization` is
deliberately off too: with it on, the first SQL connection throws
*"Globalization Invariant Mode is not supported."*

### Hand it over

1. Copy `ExtractConsole.exe` and `appsettings.json` into a folder — anywhere the person
   can write, e.g. `C:\Tools\ExtractConsole`. **Keep them together**; the exe reads the
   json from its own directory.
2. Double-click the exe.
3. Windows SmartScreen may warn on an unsigned binary from a network path. Copying it
   locally first usually avoids this. If your environment requires signed executables,
   get it signed — do not tell people to click through the warning as a habit.

### What each person needs

| | |
|---|---|
| Network | Reach to `roeeps914q:57015`, `ropeps913l01\epsidev`, `\\ropeps913L01\FTP\Strata`, and `\\mfad.mfroot.org\rchapp\erp\...\stratajazz\archive\clinical` |
| SQL — read | `SQLAgentReaderRole` in `msdb` (job status and history; also lets `sp_help_job` report the step currently executing — without it the console still works but is slower to name the right step at the start of a run) |
| SQL — act | `SQLAgentOperatorRole` in `msdb`, or ownership of the job (start/stop) |
| SQL — recon | `SELECT` on the `EPSI_ODS` recon tables |
| File share — AdHoc lists | **Modify** on the `AdhocHARList - *.csv` files in the FTP share, for anyone who will append or remove entries. The console writes as the person using it, so NTFS is the permission boundary; without it the tab shows the lists and refuses the write |
| SQL — volume | `SELECT` on `epsi_staging.dbo.etl_audit_log`, same data instance. Without it only the Extract volume tab reports a permissions message |
| SQL — upserts | `SELECT` on `STRATA_AUDIT.dbo.DailyReconCurrentProcessLog`, same data instance. Without it only that one tab reports a permissions message |
| SQL — diagnostics | `SELECT` on `EpsiSsisHost.diag.FailureRule` (same control instance); `INSERT`/`UPDATE` to add rules |
| SQL — SSIS | `ssis_admin` on `SSISDB`, or being the principal that ran the package. Without it the SSIS tab shows nothing — the catalog hides other people's executions — and the rest of the console is unaffected |

None of these are granted by the app. If someone lacks one, the affected panel shows a
permissions message and the rest of the console keeps working.

To grant them, edit the `:setvar Principal` line at the top of
[`sql/05_grant_permissions.sql`](../sql/05_grant_permissions.sql) — prefer an AD group
over named users — and run part 1 on the control instance and part 2 on the data
instance. For a view-only user, skip the `SQLAgentOperatorRole` line; every read panel
still works. Nothing in that script needs or grants `sysadmin`.

### Roles: who sees what

The `Access` section of `appsettings.json` gives each person one of three roles. Entries
are AD groups or single accounts, the widest role wins, and anyone in none is refused.

| Role | Who (today) | Sees | Can change anything? |
|---|---|---|---|
| **Operator** | `MFAD\SQL_EPMA` — the development team | every tab, DEV and PROD | yes |
| **Viewer** | the manager — add their account to `Viewers` | every tab, **PROD only** | no |
| **Finance** | the finance AD group, once confirmed | Run history, Extract volume, Counts & reconciliation, Transferred files — **PROD only**, nothing above the tabs | no |

The role is enforced **on the server, on every API call**, not only in the page. A finance
user asking for the AdHoc or restart endpoints directly gets a 403. The page also leaves out
what the role can't use: no restart buttons for a viewer, no status panel for finance. The
console window and the page header both show the role and which entry granted it.

> **In the per-PC version, roles are a convenience, not a security boundary.** The config
> file sits beside the exe on the user's own machine, so a determined user could edit it.
> What actually stops a viewer restarting PROD is that their SQL login has no operator
> role. So for the manager on the per-PC exe, give **read-only grants**: run
> `sql/05_grant_permissions.sql` for their account *without* `SQLAgentOperatorRole`, without
> Modify on the AdHoc files, and without INSERT on the catalogue.
>
> Finance should **not** be given the per-PC exe. It would need each finance user to hold
> read rights on `msdb`, `epsi_staging` and `EPSI_ODS` directly, which is far more than
> four tabs, and includes patient-level rows in the recon tables. Finance belongs on the
> hosted version, where the console connects as a service account and the config is out
> of users' reach (option A in `Hosting-Brief.docx`).

One thing a viewer won't see without an extra grant: the SSIS packages tab needs
`ssis_admin` on `SSISDB`, or it shows nothing. That's a catalog rule, not the console's.

### Verify before trusting it

```powershell
pwsh -File tests\Invoke-IntegrationTest.ps1
```

54 checks against a disposable fixture job: T23–T29 cover the unattended checker, T30–T34
the Recon_Current upsert panel, T35–T41 the AdHoc lists (against temp copies only), T42–T45 the volume tab, T46–T47 split files (console and checker must agree), T48–T53 the roles, T54 child-job placement after steps are renumbered. The production extract is never started or stopped —
T22 asserts it. Expect `54 passed, 0 failed`.

---

## B. Hosted — a shared URL (not done; read this first)

`--serve` exists and binds all interfaces, but **it is read-only unless you also pass
`--allow-actions`**, and that is on purpose.

### The problem to solve first

In portable mode the app connects to SQL as the person sitting at the keyboard. In
hosted mode it connects as **whatever account the process runs under**. So with
`--serve --allow-actions`:

- Every visitor who can reach the URL can restart the extract.
- Every action is attributed in SQL Agent to the service account, not to a person.
- The console's own audit log records the *server's* identity, not the visitor's.

That is the exact model the project deliberately avoided. Do not deploy it that way for
a group.

### What a real hosted deployment needs

1. **Authenticate the visitor.** Add `Microsoft.AspNetCore.Authentication.Negotiate` and
   require Windows auth on every endpoint.
2. **Authorise the action.** Decide which AD group may start/stop, and check membership
   server-side in `JobService` — not in the UI.
3. **Attribute the action.** Either impersonate the caller for the SQL connection
   (needs Kerberos constrained delegation, because the service account → SQL hop is the
   classic double-hop) or keep running as the service account and make the audit log
   authoritative by recording `HttpContext.User`.
4. **Pick a host.** **Not** `ROEEPS914Q` or any other SQL box — nothing may be stored on
   the SQL Server hosts. It needs a real application server that someone owns.
5. **Run it as a service.** `sc create` or NSSM, with a managed service account.
6. **Decide on TLS.** Plain HTTP carries the Windows auth handshake; on an internal
   network that may be acceptable, but it is a decision to make, not to skip.

Steps 1–3 are where the work is. Until they are done, `--serve` is useful for exactly
one thing: a read-only status board on a wall screen.

```powershell
# a read-only status board - safe
ExtractConsole.exe --serve --port 8080
```

---

## Configuring environments

`appsettings.json` ships DEV and PROD complete and enabled. An entry with
`"Enabled": false` is listed greyed-out in the UI and **refused by the API**, so a
half-filled entry cannot quietly point at the wrong instance — use that while adding a
new environment. Each entry takes:

| Field | What goes in it |
|---|---|
| `ControlServer` | Instance running SQL Agent and SSISDB. Include the port if non-default — `host,port`. |
| `DataServer` | Instance holding `EPSI_ODS`. On DEV this is a **different box** from `ControlServer`. |
| `FileShare` | Staging path the extract writes to. Used by reconciliation only. |
| `ArchiveShare` | CLEO's archive, listed by the Transferred files tab. |
| `JobName` | The parent job. |
| `ChildJobs` | Step → child job names. This is also the **action allowlist**: a job not listed here (or as `JobName`) cannot be started or stopped. |
| `DisabledSteps` | The *reason* a step is commented out. Whether it actually is, is read from the step body; a stale entry is flagged in the UI, not believed. |
| `Ssis` | Catalog folder / project / package for the SSIS step. |
| `ReconTables` | Recon table ↔ output file map. `{date}` expands to `MMddyyyy`. |
| `IsProduction` | Adds the warning chrome and the extra start confirmation. |

Then set `"Enabled": true`.

Confirm the child jobs, step numbers and recon tables actually exist in the new
environment before copying another environment's lists across — DEV and PROD already
differ in all three. The console reports a missing child job rather than inventing one,
but a wrong recon table just shows as *No data* and is easy to miss.

### Where this app's own objects live

Everything the app creates lives in the `diag` schema of **`EpsiSsisHost` on the control
instance**.

| Object | Script | Holds |
|---|---|---|
| `diag.FailureRule` + `diag.FailureSolution` | `20_`, `21_` | the shared failure catalogue |
| `diag.CheckRun` + `diag.CheckCount` | `22_` | what the unattended checker found, and the counts it compares against |

| | 20_ / 21_ | 22_ / 23_ |
|---|---|---|
| DEV `roeeps914q,57015` | installed | installed; job created **disabled** |
| PROD `roeeps014q` | run when ready | run when ready |

Set by `DiagnosticsServer` / `DiagnosticsDatabase` per environment; `DiagnosticsServer`
defaults to that environment's `ControlServer` when left blank. Point two environments
at one instance to share a single catalogue.

> **`EpsiSsisHost` is scheduled for decommission in early 2027.** All of it is reached
> through those two settings rather than a hard-coded connection, so moving it is a
> configuration change plus a re-run of `20_`–`22_` against the new home. Nothing in the
> application code names the database.

---

## The checker — nothing is deployed anywhere

The console runs from a laptop. The checker cannot — a laptop is not on at 07:00. There
is nowhere to put it either:

- **Not the SQL box.** SQL Agent runs *on* the SQL Server, so "install it on the Agent
  host" and "put a binary on the SQL server" are the same sentence. Nothing may be
  stored there.
- **Not the StrataJazz share.** That belongs to the CLEO team.
- **Not `\\ropeps913L01\FTP\Strata`.** The proxy reaches it, but it is the CLEO/FTP drop
  zone: an executable where the transfer process can write it, launched by a privileged
  account, is a way to get arbitrary code running as `MFAD\WA03749`.

So **the checker is carried inside its own Agent job step** — exactly what extract steps
3, 9, 10, 11 and 14 already do. Nothing is deployed, nothing needs approving, nothing is
left on anyone else's disk.

```
sql\checker.ps1                  <- the source of truth. EDIT THIS.
sql\Build-CheckerJobScript.ps1   <- embeds it into the job scripts
sql\23_create_checker_job_DEV.sql    } generated, one per environment,
sql\23_create_checker_job_PROD.sql   } with the environment baked in
```

```
1. sql\22_install_checker.sql    on the control instance
2. set the recipients in $AllConfig in checker.ps1   <- empty = mails nobody
3. pwsh -File sql\Build-CheckerJobScript.ps1
4. run the .sql matching the instance                <- creates the job, DISABLED
5. EXEC msdb.dbo.sp_start_job @job_name = N'DBA - CLARITY Extract Checker';
      read the step output; then enable the job
```

One file per environment rather than a variable to edit: the two host names differ by a
single character and this project has already been bitten by that.

**Run the generated `.sql` in SSMS with SQLCMD Mode off (the default), or with
`sqlcmd -x`.** Without `-x`, sqlcmd tries to expand the PowerShell's `$(...)` as its own
scripting variables and fails with `Syntax error ... near command '$'`.

### Four traps, all hit getting this working

| Symptom | Cause |
|---|---|
| `Unable to start execution of step 1 (reason: line(NNN): Syntax error)` | SQL Agent runs **token replacement** over a step's command, where `$(...)` means a job token like `$(ESCAPE_SQUOTE(JOBNAME))`. A PowerShell subexpression is an unrecognised token and the step never parses. Use concatenation or `-f`. `Build-CheckerJobScript.ps1` now refuses to generate a script containing `$(`. |
| Step goes green, history says *"The step did not generate any output"* | Calling `exit` inside an Agent PowerShell step throws the captured output away. The script is wrapped in a function that `return`s instead — a checker whose findings vanish is worse than no checker. |
| `Test-Path '\\server\share'` returns false though the file is plainly there | A step starts in the `SQLSERVER:\` provider, where a bare UNC path does not resolve. It reads exactly like a permission denial. Prefix `FileSystem::` throughout. |
| `Error reading the C:\Windows\TEMP\ directory` | The proxy's `%TEMP%` is writable but **not enumerable**. Don't set the working directory there, and don't run a single-file .NET exe as that account — it unpacks native libraries into `%TEMP%`. |

The step runs under the **`PowerShellExecution` proxy** — `MFAD\WA03749` on DEV,
`MFAD\WA03747` on PROD. There is no CmdExec proxy on either instance. The subsystem is
**Windows PowerShell 5.1**, so `checker.ps1` avoids PS7-only syntax. That account needs:

| | |
|---|---|
| `msdb` on the control instance | read — job and step history |
| `EPSI_ODS` on the data instance | read — the recon tables |
| `EpsiSsisHost.diag` | read **and write** — the checker records what it found |
| the data file share | read — counting records means opening the files |
| Database Mail | `DatabaseMailUserRole` in `msdb`, once recipients are set |

Verified on DEV 2026-10-09: the job runs green in 12 seconds under `MFAD\WA03749` with
the full report in its step history, and nothing installed anywhere.

PROD needs `20_`/`21_`/`22_` run against `roeeps014q` and then
`23_create_checker_job_PROD.sql`. None of that has been done.

> **Separately — not this project's objects, but on the same doomed database.**
> [`SQL-agent-capture-failures-log-into-servicenow`](../../SQL-agent-capture-failures-log-into-servicenow)
> still has 3 tables, 1 view and 4 procedures in `EpsiSsisHost.snow` on `roeeps914q`
> (`JobFailureTicket` 39 rows, `IntegrationLog` 7, `WatchedJob` 1). Deliberately left in
> place for now; nothing is broken today.
>
> It will **not** be a lift-and-shift. `snow.vw_JobFailure` and the detection procedures
> join `msdb.dbo.sysjobhistory`, `sysjobs` and `sysjobsteps` directly — cheap today only
> because `EpsiSsisHost` sits on the same instance as `msdb`. Rehome them to any database
> on a *different* server and those joins cross a server boundary; there is **no linked
> server** from the data instance back to the control one (a 4-part name fails with
> "Could not find server 'ropeps913l01\epsidev' in sys.servers"). So the move needs a
> decision first: keep them on an instance that has `msdb` alongside, create a linked
> server, or restructure so the PowerShell orchestrator — which already runs on the
> control host and can reach both — reads `msdb` locally and writes the tracking rows
> remotely.
>
> `EpsiSsisHost.dbo` also holds `EPSI_GEN_SSIS_ExecutePackage` and `Mayo_Run_Agent_Job`,
> which belong to neither project and are presumably someone else's to rehome.

### Reference values

Both environments ship configured and enabled. There is no TEST instance.

| | DEV | PROD |
|---|---|---|
| Control instance | `roeeps914q,57015` — **named** instance; the default on 1433 is a decoy with no jobs | `roeeps014q` — **default** instance on 1433 |
| Data instance | `ropeps913l01\epsidev` | `ropeps013L01` |
| File share | `\\ropeps913L01\FTP\Strata` | `\\ropeps013L01\FTP\Strata` |
| Steps | 15 | 14, **in a different order** (steps inserted 2026-10-08 and 2026-10-22; the console reads step bodies rather than trusting numbers) |
| Schedule | ad hoc, disabled | Daily 07:00, enabled |
| Steps commented out | 2, 7, 8, 12 | none (step 11, *Copy Files to CLEO*, looks disabled but is live) |
| SSIS packages per run | ~25, several steps currently off | 28 across 4 catalog projects |
| `IsProduction` | false | **true** |

> Child job names are spelled differently in each environment and both spellings are
> real: DEV has `Resfresh Deecription Tables` and
> `Resfresh Tables Sourcing from BigQuery`; PROD has `Refresh Description Tables` and
> `Refresh Tables Sourcing from BigQuery`. The config matches the server, typos included
> — correcting them here would break the child-job panel and the action allowlist.

### What `IsProduction` changes

Set on PROD. It adds a standing red banner naming the instance, outlines the filter bar,
and **requires the caller to echo the environment name before a job will start** —
enforced in `Program.cs`, not only in the browser, so it holds against a scripted
request too. Tests T14/T15 cover both halves. It changes nothing else; the allowlist and
the stop confirmation apply everywhere.

---

## Security notes

- **No credentials anywhere.** Integrated auth only; `appsettings.json` holds no
  secrets and is safe in source control.
- **Loopback by default.** Nothing is listening on the network unless you pass
  `--serve`.
- **Per-launch token.** A random token is required on every API call, so another
  process on the same machine cannot drive the console. It is stripped from the address
  bar after load and changes every launch.
- **Action allowlist.** Only the configured job and its children can be started or
  stopped, enforced server-side (test T03).
- **Stop requires typed confirmation**, echoed and checked on the server, not just in
  the browser.
- **Read-only by default when shared.** `--serve` disables actions unless
  `--allow-actions` is explicit.

---

## Uninstalling

Delete the folder. Then optionally remove the local audit log at
`%LOCALAPPDATA%\ExtractConsole\`. Nothing is written to the registry, no service is
installed, and nothing on any SQL Server is modified — the console only ever calls
`sp_start_job` and `sp_stop_job`.
