# Daily extract console

A browser-based console for the SQL Agent job
**`Data Extract From CLARITY for STRATAJAZZ`** — status, step-level diagnosis,
restart-from-a-step, stop, reconciliation and run history. The same binary also runs
[unattended as a checker](#the-checker----check) that mails you when something is wrong,
so nothing depends on someone remembering to look.

It is **a single `.exe` you double-click**. It starts a web server bound to
`127.0.0.1`, opens your browser, and talks to SQL Server as *you*. No host server,
no installer, no admin rights, no service account, no firewall change.

```
your workstation
  ExtractConsole.exe
     ├── Kestrel on http://127.0.0.1:<free port>
     ├── opens your default browser
     │
     ├─ DEV ──► ROEEPS914Q\EPSIDEV:57015   msdb + SSISDB   (control plane)
     │          ROPEPS913L01\EPSIDEV       EPSI_ODS        (recon counts)
     │          \\ropeps913L01\FTP\Strata  output files
     │
     └─ PROD ─► ROEEPS014Q                 msdb + SSISDB   (default instance, 1433)
                ROPEPS013L01               EPSI_ODS
                \\ropeps013L01\FTP\Strata  output files

SQL Agent, on the control instance   (sql\checker.ps1, inline, every 30 min)
  DBA - CLARITY Extract Checker  ──► the same reads, then sp_send_dbmail
```

> **Status: console working against DEV and PROD.** 54/54 integration tests pass against
> a disposable fixture job. The **checker is installed on DEV and disabled**, pending
> validation; PROD has neither the checker nor the failure catalogue yet. There is no
> TEST instance.

---

## Why it is built this way

**Why a local exe rather than a hosted site.** The brief was a web app with no
dedicated host server. A loopback-bound exe satisfies that literally: there is nothing
to procure, provision or patch, and because every SQL connection uses Windows
integrated auth, the operator's own permissions are the authorisation boundary and
SQL Agent's history already names a real person. A hosted version would need a service
account with rights to start the extract, plus app-level roles to decide who may use
them — a different and much larger security conversation. The code keeps that door open
(`--serve`) but it is deliberately read-only by default.

**Why polling rather than a push.** SQL Agent has no change feed. The console reads
`msdb` on a timer (15s by default) and holds the previous render while refetching, so
there is no skeleton flash. Reads are cheap: small queries against indexed `msdb`
tables, ~50 ms for a full status refresh.

**Which step is running takes three sources, in order.** This is the one piece of state
SQL Agent makes genuinely awkward, and getting it wrong produces a self-contradicting
screen:

1. **`sp_help_job`'s `current_execution_step`** — Agent's live view, correct the instant
   a run starts. Only trusted while `current_execution_status` is *executing* or
   *between retries*; it reads `0 (unknown)` when idle. Queried only while a job is
   running, and any failure (typically a missing `SQLAgentReaderRole`) silently falls
   through to the next source.
2. **the step after the highest *finished* step in this run** — Agent writes a step's
   history row when the step *finishes*, so this is sound once anything has completed.
   Retry rows (`run_status 2`) are not finishes, so a retrying step stays current.
3. **`sysjobactivity.last_executed_step_id`**, but *only* while it names a step that has
   not yet finished.

Source 3 looks like the obvious answer and is a trap: it names the last step Agent
**executed**, which after that step completes is a step that is already *done*. Taking
it at face value reported "Running — step 2" while step 2's own row said *Succeeded*.
Tests T10 and T16 pin both halves of this down.

**Next scheduled run comes from `sysjobactivity`, not `sysjobschedules`.** Same class of
trap. `sysjobschedules.next_run_date` is not refreshed when a run finishes, so for the
rest of the day it still names the run that already happened — displaying a "Next
scheduled" identical to "Started". `sysjobactivity.next_scheduled_run_date` is Agent's
live calculation. On top of that, any next-run value that is already in the past is
discarded rather than shown, at both job and schedule level (T17).

**Why an allowlist on actions.** `sp_start_job` will happily run anything the caller
has rights to. The API resolves every requested job name against the configured parent
and its child jobs and rejects everything else, so the console cannot be turned into a
general-purpose job runner — even by someone crafting their own HTTP request. This is
covered by test T03.

Worth knowing if you touch configuration loading: `--config` **removes** the JSON
sources the host registers by default before adding the named file. Layering a second
settings file on top instead would be quietly dangerous, because .NET merges
configuration arrays *by index* — an environment defined in the second file would
inherit whatever `ChildJobs` entries the first file left at higher indices, extending
the allowlist with jobs nobody configured. T03 asserts the allowlist contains exactly
the configured jobs.

---

## What it shows

| Panel | What it answers |
|---|---|
| **Current state** | Running or not, which step, how long, who started it, when it is next scheduled |
| **Steps** | Every step with per-step outcome, duration, the full error text, and the child jobs each orchestrator step launches |
| **Extract volume** | Rows each staging package pulled per day, for any date range — a total line, a summary with an influx flag (last 7 loading days vs the 7 before), and one small chart per dataset on its own scale. Days that *succeeded with 0 rows* are marked and named |
| **Run history** | Duration per run as a chart plus the same data as a table, with drill-down into any past run's steps |
| **Counts & reconciliation** | ODS recon table rows against the extract's own file counts, with an on-demand true record count |
| **Transferred files** | What CLEO actually moved to and from Strata on a given day, from the archive |
| **AdHoc lists** | The ten `AdhocHARList - *.csv` files the next extract re-pulls. Append extra entries (typed, pasted, or uploaded as pipe-delimited text or `.xlsx`) or remove one added by mistake — validated line by line, previewed, refused while the extract runs, typed confirmation on PROD. In the Charge and Payments lists the transaction is optional: an encounter on its own is written `1234567890|` |
| **Recon_Current upserts** | What the Upsert step did to each dataset on the selected day — how the total split into updates and inserts, when each finished, and the error text when one failed. The day picker marks which days had failures |
| **SSIS packages** | Every package the extract runs (~28, mostly launched by child jobs) matched to the selected run, with errors and warnings |
| **Known failures** | Shared catalogue of what this job's errors mean, each with an ordered list of fixes to try; matched explanations appear on the failing step |

### Two things it will tell you that SSMS will not

**Some steps are no-ops.** Their bodies are commented out and they short-circuit with
`IF (1=1) ... RETURN`, so they report **success** to SQL Agent and Job Activity Monitor
paints them green. On DEV that is steps 2, 7, 8 and 12 — which is why DEV produces no
surgical or Xifin output. On PROD it is currently none.

The console decides this by **reading each step's own T-SQL**, not from a list in
config. That matters: PROD's *Copy Files to CLEO* step carries a
`DISABLED on 2026-02-03` comment and an `IF (1 = 1) ... RETURN`, but the whole block is
wrapped in `/* */`, so the step is live. A hand-maintained list called it disabled and
was wrong — reading the body gets it right and keeps getting it right as steps are
switched on and off, and as their numbers shift. `DisabledSteps` in
config now only supplies the human reason, and a note that disagrees with the step is
flagged in the UI as a *config mismatch* rather than believed.

One caveat, since `msdb` does not keep historical step definitions: on an old run the
no-op tag reflects how the step stands **today**, not how it stood then.

**Orchestrator steps hide the real work.** Several steps call `sp_start_job` and wait.
When one fails, the useful question is *which of* `HB Table Extract From Epic` /
`PB Table Extract From Epic` broke, and at which of **its** steps. Expanding the step
answers that.

**Reconciliation answers two questions, and neither is allowed to hide the other.**
*Did every row reach the file?* is answered by counting records; *is the file well
formed?* by whether lines equal records. The files are written straight out of the ODS
tables, so a file can never legitimately hold more records than its table - more *lines*
than rows means a field value contains an embedded CR/LF splitting one record across
two. Reporting that as a mismatch sent people hunting a load failure that was never
there; reporting it as a clean match once the records are counted would be the opposite
mistake, retiring a real defect. So a dataset whose records reconcile but whose file is
split stays amber, showing how many continuation lines it has. Only a file that is both
complete and one-record-per-line reads Match.

**And zero is not a match.** An empty table reconciles perfectly against an empty file,
so an extract that produced nothing used to score eleven green ticks - which is exactly
what happened on PROD 2026-10-08, when every step reported success, staging came back
empty, and header-only files went to Strata declaring 0 records. An empty dataset now
reads *No rows at all* in red. On this job a green job status means the steps ran, not
that any data moved; the reconciliation panel is the only thing that answers the latter.

The *Declared to Strata* column is the figure from `MayoClinic_filecounts_MMddyyyy.txt`,
the count file Strata receives alongside the data. The extract builds it with
`ReadLine()`, so it is a line count and inherits exactly the same problem - on PROD
2026-10-07 it declared 379,468 PB Encounter records in a file holding 378,741. Cleaning
the source columns corrects the file, the console and that declaration in one go. See
the runbook and `sql/10_find_embedded_line_breaks.sql`.

**The extract is almost entirely SSIS, and almost none of it is visible at the top.**
The parent job has exactly one SSIS step; its child jobs have ~27 more. A PROD run is 28
package executions across four catalog projects. The SSIS tab lists all of them against
the selected run — the package list is read from the jobs' own SSIS step definitions, so
it follows the jobs rather than a config file. Packages are matched inside the run's time
window and filtered to the extract's own, so a concurrent unrelated package
(`StrataDailyRecon2` is one) is never blamed on this extract.

---

## Running it

```powershell
# build a single self-contained exe into dist\
pwsh -File build.ps1

# then just
dist\ExtractConsole.exe
```

Or during development:

```powershell
cd src\ExtractConsole
dotnet run
```

### Switches

| | |
|---|---|
| *(none)* | loopback only, opens your browser — **the normal way to use it** |
| `--port 5173` | pin the port instead of picking a free one |
| `--no-browser` | start without launching a browser |
| `--config PATH` | use a different settings file — it **replaces** `appsettings.json`, it does not layer on top of it |
| `--serve` | bind all interfaces for a shared URL — **read-only** |
| `--serve --allow-actions` | shared URL with start/stop — read [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) first |
| `--check` | run the unattended check once and exit — no web server, no browser |

`--serve` is read-only unless you also pass `--allow-actions`, because in shared mode
every visitor acts as the account that launched the process. That is fine for a status
board and wrong for a restart button.

---

## The checker — `--check`

The console only helps someone who opens it. Every serious problem this extract had in
October 2026 was a **silent success**: the job went green while shipping header-only
files on 10/08, while sending Strata 727 junk rows a day, and while steps 7 and 8 sat
commented out. The job's own notification is failure-only, so none of it reached anyone.

`--check` is the same logic running unattended. It makes one pass and exits:

| Finding | Severity | What it catches |
|---|---|---|
| `NoRecentRun` | Critical | the extract did not run at all |
| `RunFailed` | Critical | the last run failed — names the step, and the catalogue's first suggested fix |
| `RunCanceled` | Warning | someone stopped a run and nothing restarted it |
| `EmptyExtract` | Critical | every recon table at zero — the 2026-10-08 signature |
| `DatasetEmpty` | Critical | *one* table at zero that had data last time — the same failure, one dataset at a time |
| `ReconShort` | Critical | the file holds fewer records than the table |
| `SplitRecords` | Warning | embedded CR/LF splitting records across lines |
| `VolumeDrop` | Warning | a dataset below `VolumeDropFactor` of its last recorded count |
| `StillRunning` | Info | mid-flight — the data checks are skipped rather than measured half-loaded |

```powershell
# see what it would say, change nothing
ExtractConsole.exe --check --env DEV --dry-run
```

Three deliberate choices:

- **It exits 0 when it *finds* something.** The e-mail is the alert. A non-zero exit
  means the *checker itself* broke, which fails its Agent job and reaches you through the
  ordinary path. Keeping those apart is the point. `--fail-on-critical` inverts it if you
  want the opposite; exit 3 is always "could not check".
- **It reports each run once.** The schedule fires every 30 minutes; the dedupe keys on
  the run instance, so you get one mail per bad run, not fourteen. `--force` overrides.
- **`NoRecentRun` only fires where a schedule is enabled.** DEV is started by hand with
  its schedules off, so "has not run for 29 hours" there is a fact about the week, not a
  fault — and an alert that always fires is an alert nobody reads.

Naming an environment that is unknown, or present but `Enabled: false`, is an error
(exit 3), not a quiet no-op. A scheduled checker that checks nothing while going green
every tick is the one outcome worse than no checker.

### Where it runs: nowhere, and that is the point

There is no laptop on at 07:00, nothing may be stored on the SQL Server hosts (SQL Agent
runs *on* the SQL server — there is no separate agent machine), and the StrataJazz file
share belongs to the CLEO team. So the checker is **carried inside its own Agent job
step**, which is exactly what extract steps 3, 9, 10, 11 and 14 already do. Nothing is
deployed, nothing needs approving, nothing is left on anyone else's disk.

```
sql\checker.ps1                  <- the source of truth. EDIT THIS.
sql\Build-CheckerJobScript.ps1   <- embeds it into the job scripts
sql\23_create_checker_job_DEV.sql, _PROD.sql   <- generated; environment baked in
```

```
1. sql\22_install_checker.sql    diag.CheckRun + diag.CheckCount
2. set the recipients in $AllConfig in checker.ps1   <- empty = mails nobody
3. pwsh -File sql\Build-CheckerJobScript.ps1
4. run the .sql matching the instance   -> "DBA - CLARITY Extract Checker", DISABLED
5. validate by hand, then enable
```

The C# `--check` above and `sql\checker.ps1` are the same checks in two languages: the
console's version for running by hand, and the inline one the schedule runs. They are
kept deliberately identical — **change one, change the other**. See
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the four SQL Agent traps this hit.

It is a **separate job**, not a step appended to the extract, for three reasons: both
extracts end on a quit-reporting-success step so an appended step would never run (the
same trap that leaves PROD's Xifin unreachable), step numbers move (PROD gained one on
2026-10-08), and only a separate job can notice that the extract did not run at all.

---

## Restarting from a step

This is the feature that matters most here, because the extract takes **3–11 hours** and
recent history is roughly half failures. Agent supports it natively
(`sp_start_job @step_name`), and every step is set to *Quit the job reporting failure*,
so a failure always stops at a known step.

The console offers a restart button on the failed step and on every step in the list.
Two things it makes explicit in the confirmation, because both have bitten people:

- Restarting at step *N* **skips steps 1 to N−1 entirely** — including step 1, the
  "extract STARTED" e-mail. Recipients will see a COMPLETED mail with no STARTED mail.
- Every step is *go to the next step* on success, so a restart at step 5 runs **5
  through 14**, not step 5 alone.

---

## Layout

```
src/ExtractConsole/
  Program.cs                  Host, CLI switches, token gate, the API surface
  Config/AppConfig.cs         Settings model, including the action allowlist
  Models/Models.cs            Wire types + the shared outcome vocabulary
  Services/
    Db.cs                     Connection factory; HHMMSS duration decoding
    JobService.cs             msdb reads, run slicing, start / stop      <- the core
    SsisService.cs            SSIS catalog executions and messages
    ReconService.cs           Table rows vs the extract's own line counts
    FileService.cs            Output file listing
    UpsertLogService.cs       STRATA_AUDIT Recon_Current upsert log, kept in full
    AdhocListService.cs       Append to / remove from the AdHoc HAR lists; reads .xlsx
    VolumeService.cs          Daily rows_extracted per dataset from epsi_staging.etl_audit_log
    AccessControl.cs          Operator / Viewer / Finance roles from AD groups; per-endpoint gate
    DiagnosticsService.cs     The shared failure catalogue and its matching
    StepBody.cs               Reads a step's body to tell commented-out from live
    CheckService.cs           The unattended check behind --check
    AuditLog.cs               Append-only local record of every action
  wwwroot/                    index.html, app.js, styles.css  (no build step)
  appsettings.json            Environments + the Check block              <- EDIT

sql/
  05_grant_permissions.sql       Least-privilege grants for an operator or AD group
  10_find_embedded_line_breaks.sql  Diagnostic: CR/LF/TAB in file-producing ODS columns
  11_fix_embedded_line_breaks.sql   Remediation (dry-run by default) + where the real fix belongs
  12_check_sample_record.sql        Tracks one encounter through the fix (tracer: 1190289930)
  20_install_diagnostics.sql        Shared failure catalogue (EpsiSsisHost.diag) + 11 seeded rules
  21_add_failure_solutions.sql      Ordered candidate fixes per rule (29 seeded)
  22_install_checker.sql            diag.CheckRun + diag.CheckCount - the checker's history
  checker.ps1                       The unattended checker        <- SOURCE OF TRUTH
  Build-CheckerJobScript.ps1        Embeds checker.ps1 into the job scripts below
  23_create_checker_job_DEV.sql     GENERATED - Agent job, created DISABLED
  23_create_checker_job_PROD.sql    GENERATED - same, targeting PROD
  Invoke-LineBreakCleanup.ps1       Runs the scan/fix pair as scan -> fix -> scan

tests/
  Invoke-IntegrationTest.ps1  29-check suite against a disposable fixture
  10_create_selftest_job.sql  Fixture: 7 steps, fails on purpose at step 2
  20_remove_selftest_job.sql  Removes the fixture
  selftest.config.json        Points the console at the fixture, never the extract

docs/
  DEPLOYMENT.md               Handing it to someone else; the hosted option
  RUNBOOK.md                  Daily use, symptom -> cause, routine changes
```

---

## DEV and PROD are not the same job

Both are configured and working, but **PROD is not DEV minus two steps** — the order
differs too. Anything that hard-codes step numbers across both will be wrong.

| | DEV | PROD |
|---|---|---|
| Control instance | `roeeps914q,57015` — **named** instance | `roeeps014q` — **default** instance, 1433 |
| Data instance | `ropeps913l01\epsidev` | `ropeps013L01` |
| Share | `\\ropeps913L01\FTP\Strata` | `\\ropeps013L01\FTP\Strata` |
| Steps | 15 | 14 |
| Schedule | ad hoc, disabled | **Daily 07:00, enabled** |
| Steps commented out | 2 (BigQuery), 7 (Surgical), 8 (Xifin), 12 (CLEO) | **none** — step 10 looks disabled but is live, and BigQuery runs |
| Output files | persist on the share | **collected and removed** by the CLEO/FTP pickup |

The host names differ by one character — `roeeps**9**14q` vs `roeeps**0**14q` — which is
why the console paints a standing red banner and outlines the filter bar when a
production environment is selected, and why starting a job on production requires
typing the environment name (checked on the server, not just in the browser).

### Step mapping

Verified against both live jobs on 2026-10-23.

| DEV | PROD | |
|---|---|---|
| 1 | 1 | Extract Start Email |
| 2 | 2 | Refresh Tables Sourcing From BigQuery — *no-op in DEV, live in PROD* |
| 3 | 3 | Refresh STG_T_UDF_PROCEDURE_FLAG Table — *migrated to PROD 2026-10-22* |
| 4 | 4 | Epic HB/PB Extract Orchestrator |
| 5 | 5 | Refresh ETL Encounter Table |
| 6 | 6 | Load HB/PB ODS Tables and Description Table orchestrator |
| 7 | 7 | Surgical File Extract — *no-op in DEV* |
| 8 | 14 | Xifin File Extract — *no-op in DEV*, **unreachable in PROD, see below** |
| 9 | 10 | Create Load Size and Count File |
| 10 | 8 | Populate Recon Tables — *runs before the count file in PROD, after it in DEV* |
| 11 | 9 | Upsert Process for Recon_Current Tables — *added DEV 2026-10-06, PROD 2026-10-08* |
| 12 | 11 | Copy Files to CLEO — *no-op in DEV, live in PROD* |
| 13 | 12 | Runtimes and Record Counts (SSIS) |
| 14 | — | Generate the list of missing parent encounters — **DEV only, pending migration** |
| 15 | 13 | Extract Complete Email |

**Step numbers move.** PROD has gained two steps this month (2026-10-08 and
2026-10-22), each shifting everything below it. So the console no longer trusts step
numbers for anything that matters: which step launches which child job is read from the
step bodies, commented-out steps are detected from their bodies, and reachability from
the job's own transitions. `ChildJobs` in config is now the allowlist of jobs the console
may touch; its step number is only a fallback, and a disagreement shows as a warning on
the step. It is also why the checker is a separate job rather than a step appended to the
extract.
The child job is also spelled `Resfresh Deecription Tables` in DEV and
`Refresh Description Tables` in PROD. Both spellings are in the config because both
are real.

### The last step in PROD never runs

`Extract Complete Email` (step 13) is set to **quit reporting success**, so the job ends
there and **`Xifin File Extract` (step 14) is never reached on a normal pass** — it only
runs if someone starts the job at step 14. The console detects this by walking the job's
own success transitions and labels the step *"never runs on a normal pass"* rather than
reporting it as a skip, which would read like a fault. Whether that is intentional is a
question for you, not something the console assumes either way.

---

## Tests

```powershell
pwsh -File tests\Invoke-IntegrationTest.ps1
```

Creates `ZZ_ExtractConsole_SelfTest` (6 steps: fails on purpose at step 2; step 4 sits
after a quit-on-success step so it is unreachable, mirroring PROD's Xifin step; steps 5
and 6 are a commented-out body and a re-enabled one to pin the no-op detector; step 7 is
an SSIS step for package discovery),
drives the real HTTP endpoints against it, and removes it afterwards. **The production
extract is never started or stopped** — T22 asserts exactly that, and the production
confirmation gate is exercised against the fixture rather than the real job.

T23–T29 run `--check` as a separate process, the way the Agent job does, rather than
over HTTP. They write to the shared `diag.CheckRun`/`diag.CheckCount` tables and delete
their own rows in the teardown. T30–T34 read the real upsert log — there is no fixture
for it, and reading it is harmless. T35–T41 write AdHoc lists, but only copies in a temp
folder: the suite rewrites its config so the real FTP share is never touched. T42–T45
read the real DEV etl_audit_log; DEV's renamed packages and its TEST row make it a good
test of the package-name mapping.

| | Check |
|---|---|
| T01 | an API call without the token is rejected |
| T02 | status reports the fixture and its seven steps |
| T03 | the allowlist holds exactly the configured jobs and nothing else |
| T04 | start runs the job and status turns to running |
| T05 | starting an already-running job is refused |
| T06 | stop without the typed confirmation is refused |
| T07 | stop with the typed confirmation stops the job |
| T08 | a run that fails is attributed to the step that broke |
| T09 | steps after the failure are reported as not reached, not succeeded |
| T10 | restart from a step starts there and reports that step immediately |
| T11 | history lists the runs with their durations |
| T12 | every action was written to the audit log |
| T13 | a step the job flow never reaches is flagged, not reported as skipped |
| T14 | on a production environment, start without the environment name is refused |
| T15 | on a production environment, start with the environment name is accepted |
| T16 | a step that has finished is never reported as the running step |
| T17 | the next scheduled run is in the future, not the run that already happened |
| T18 | a commented-out step is detected, and a re-enabled one is not |
| T19 | SSIS packages are discovered from the job step definitions |
| T20 | a catalogue rule diagnoses a failing step in place |
| T21 | an invalid rule is refused rather than stored |
| T22 | the production extract was never touched by this run |
| T23 | a run that was stopped and never restarted is reported |
| T24 | a failed run is reported as Critical, naming the step |
| T25 | a dry run records nothing |
| T26 | the same run is recorded once and not reported twice |
| T27 | `--force` overrides the suppression |
| T28 | `--fail-on-critical` turns a finding into a non-zero exit |
| T29 | an unknown or disabled environment fails rather than passing quietly |
| T30 | the upsert log reports a day, its datasets and the whole history |
| T31 | rows carry the update/insert split and map to recon dataset keys |
| T32 | failures are scoped to the selected day |
| T33 | asking for a specific day returns only that day |
| T34 | a day the environment has no rows for is reported as that day |
| T35 | all ten AdHoc lists are reported, and a missing file is flagged not created |
| T36 | a preview validates every line and writes nothing |
| T37 | an append adds at the end, keeps the header, and skips what is already there |
| T38 | a remove takes out only the listed entries and keeps the rest in order |
| T39 | an Excel upload is read, including a 15-digit TDL_ID stored as a number |
| T40 | a change is refused while the extract is running |
| T41 | on production a change needs the environment name typed |
| T42 | the volume report has the ten datasets, one point per day in range |
| T43 | no successful run is a gap, and a 0-row success is kept as 0 |
| T44 | renamed packages map to one dataset, and non-staging packages are ignored |
| T45 | a range given backwards is read forwards, and the summary is computed from data days |
| T46 | a file split into _f1, _f2 parts is counted as one dataset |
| T47 | the scheduled checker counts split files the same way as the console |
| T48 | with no Access section configured, everyone is an operator |
| T49 | an AD group in the list grants its role |
| T50 | a viewer sees every read, in its environments only, and can change nothing |
| T51 | finance gets its four tabs, nothing above them, and every other endpoint is refused |
| T52 | an account in no group is refused everything except the page explaining why |
| T53 | someone in two lists gets the wider role |
| T54 | a child job is shown under the step that names it, not the step number in config |

---

## Notes on the servers

The instance is **`ROEEPS914Q\EPSIDEV` on TCP 57015**. `ROEEPS914Q` also hosts a
*default* instance on 1433 which does **not** contain this job — connecting there by
mistake shows an empty `sysjobs` and looks like the job is gone. Config targets
`roeeps914q,57015` explicitly.

The extract's **data** does not live on the job server. `EPSI_ODS`, `EPSI_STAGING` and
`STRATA_AUDIT` are on **`ROPEPS913L01\EPSIDEV`**, and the output files go to
`\\ropeps913L01\FTP\Strata`. That is why the config has both a `ControlServer` and a
`DataServer`.

## Related

[`SQL-agent-capture-failures-log-into-servicenow`](../SQL-agent-capture-failures-log-into-servicenow)
raises a ServiceNow incident when this job fails. The two are complementary: that one
pushes a ticket to management, this one is the operator's console. Neither modifies the
extract.
"# daily-extract-support-console" 
