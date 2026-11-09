# Runbook

Day-to-day use of the extract console, and what to do when it tells you something.

---

## 1. Opening it

Double-click `ExtractConsole.exe`. A console window appears with the URL and your
browser opens on it. **Leave the console window open** — closing it stops the server.
Ctrl+C in that window, or just closing it, shuts everything down.

The URL carries a one-time token that changes every launch. If you bookmark the URL the
bookmark will stop working — bookmark nothing, launch the exe.

Everything the app does, it does **as you**. If you cannot start the job in SSMS, you
cannot start it here either, and the error will say so.

---

## 2. The morning check

1. **Current state** — green *Succeeded* with a sensible duration means you are done.
2. If it says **Failed**, the hero line names the step that broke. Click that step for
   the error text.
3. Switch to **Counts & reconciliation**. The date auto-follows the last run. Every row
   should say *Match*.
4. **Output files** — confirm today's stamp (`MMddyyyy`) on the files you care about.

**A run that says *Succeeded* can still be wrong, and that is the normal failure mode
here, not the exception.** Steps whose body is commented out report success while doing
nothing. A step can succeed while producing fewer rows than expected. And on 2026-10-08
the whole PROD extract succeeded end to end having moved **no data at all**. The job
status tells you the steps ran; only the reconciliation panel tells you anything
arrived.

> Steps 1–4 are what the **checker** (section 8) does for you on a timer, so the morning
> check becomes "did I get a mail?" rather than "did I remember to look?". Opening the
> console is still how you act on what it found.

---

## 3. Symptom → cause

### "Succeeded" but a step is tagged *body commented out*

Expected. The step's body is commented out and returns immediately, so it reports
success to SQL Agent and SSMS shows it green.

**The console works this out by reading the step's own T-SQL**, not from a list in
config, so it stays right when a step is switched on or off. As of 2026-10-06:

| Environment | Steps commented out | What they would do |
|---|---|---|
| DEV | 2, 7, 8, 11 | BigQuery refresh; **Surgical File Extract**; **Xifin File Extract**; Copy Files to CLEO |
| PROD | *none* | — |

Two things that catch people out:

- **Nothing is disabled in PROD.** Step 9 (CLEO) *looks* disabled if you skim it — there
  is a `DISABLED on 2026-02-03` comment and an `IF (1 = 1) ... RETURN` — but that whole
  block is wrapped in `/* */`, so the `sp_start_job` above it runs. Step 2 (BigQuery) is
  live too, unlike DEV.
- **DEV's Surgical and Xifin steps are off.** That is why DEV produces no surgical file
  and its recon table is empty. Not a failure.

If a step is a no-op and a child job is listed under it, the console warns that the step
did **not** launch that job — whatever state the child shows came from another trigger.

To re-enable: uncomment the body in the job step. The console picks the change up on the
next poll; the `DisabledSteps` entry in `appsettings.json` only carries the reason, and
if it disagrees with the step the console shows a *config mismatch* tag so the note gets
cleaned up.

### The running step number looks like it is lagging

It should not. If you ever see *"Running — step N"* while step N's own row shows
*Succeeded*, that is a bug — it was one, and it is fixed and regression-tested
(T10, T16). Report it with a screenshot.

Note that *Current state* names the step **in flight**, so it moves on the moment the
previous step records its outcome. There is a sub-second window at each hand-off where
the named step has been chosen but has not quite begun; that is Agent's own timing, not
a stale read.

### *Next scheduled* says nothing

Two legitimate reasons:

- **DEV** has only the *ADHOC Schedule* and it is **disabled**, so there is no next run
  to report. DEV is started by hand.
- The job has no enabled schedule at all.

What it will no longer show is a next-run time that has already passed. Agent does not
refresh `sysjobschedules.next_run_date` the moment a run ends, so that column keeps
naming the run that just finished — which used to display as a *Next scheduled*
identical to *Started*. The console now takes Agent's live figure and discards anything
in the past (T17). PROD should read the next 07:00.

### A step says *"never runs on a normal pass"*

In PROD, `Extract Complete Email` (step 13) quits reporting success, so the job ends
there and **`Xifin File Extract` (step 14) is never reached**. It only runs if someone
starts the job at step 14 deliberately.

This is not something the console caused and not necessarily wrong — but it does mean a
green PROD run has not run Xifin. If Xifin is supposed to run daily, step 13's
*On success action* needs changing to *Go to the next step*.

> Those numbers were 11 and 12 until 2026-10-08, when *Upsert Process for Recon_Current
> Tables* was inserted at step 8. The console walks the job's own transitions rather than
> trusting a number, so it followed the change; this page had to be told.

### A step failed but the message is unhelpful

Steps 4, 5, 6, 7 and 8 are orchestrators — they start a child job and wait, so their own
message only says the child did not succeed. **Expand the step**: the child jobs are
listed underneath with their own outcome and the step *within the child* that failed.

### Diagnosing an SSIS failure

Nearly all the real work is SSIS: the parent job has one SSIS step, and its child jobs
have about 27 more — a PROD run is **28 package executions** across four catalog
projects.

Open the **SSIS packages** tab. It lists every package the extract runs, grouped by the
job that launches it, matched to the selected run. Click any row to pull its errors and
warnings from the catalog.

- The list is read from the jobs' own SSIS step definitions, so it follows the jobs. If
  a child job is missing from it, that job is probably absent from `ChildJobs` in
  `appsettings.json`.
- Only the extract's own packages appear. Other SSIS work on the same instance is
  excluded so it cannot be mistaken for an extract failure.
- **"did not run"** is not automatically a fault — it is expected for any package whose
  step was not part of that pass, which is normal after a restart-from-step, and normal
  on PROD for `MMLVolumeMCCStrata.dtsx` (the Xifin package, whose step sits after the
  quit-on-success step).
- Durations here are the quickest way to see what dominates a slow run. On 2026-10-07
  `LoadStagingHBEncounterTableOptimize` took 55m of a 1h48m run.

If the tab shows a permissions error, you need `ssis_admin` on SSISDB or to be the
principal that ran the packages.

### Reconciliation shows a mismatch

Read the status word before anything else — they mean very different things:

| Status | Meaning |
|---|---|
| **No rows at all** | Table and file are both **empty**. They agree, which is exactly why it is not a match — the extract produced nothing. See below. |
| **Short** | Fewer records than table rows. Something really is missing. Investigate. |
| **Extra lines** | More *lines* than rows. Not possible by losing data — a field contains an embedded line break. |
| **Rows OK, file split** | Records reconcile, but the file still has records spanning several lines. |
| **Match** | Complete *and* one record per line. |

#### "No rows at all" — the silent failure

Zero reconciles perfectly against zero, so an extract that produced nothing used to show
eleven green ticks. It no longer can.

This is not hypothetical. On **2026-10-08** the PROD run finished in 9m42s (normally
~1h48m) with **every step reporting success**, having loaded nothing: staging empty, ODS
empty, header-only files shipped to Strata, and a count file declaring 0 records. The
`HB Table Extract From Epic` child job completed in 21 seconds across six SSIS packages
that normally take over an hour.

If you see this: the job's own status will say *succeeded* and the ServiceNow logger will
not have fired, because nothing failed. Check in this order — staging row counts
(`EPSI_STAGING.dbo.STG_T_*`), then the Epic extract child jobs' durations against a
normal day, then whether Strata has already consumed the empty files.

#### Picking an older extract date

The ODS recon tables hold **only the most recent load**. Selecting an earlier date
compares that day's file counts against today's tables, which means nothing — the panel
shows a red banner when the selected date is not the last run's date. Believe the banner,
not the Difference column.

The *Table rows* column is a metadata count by default — instant, and accurate in normal
operation, but it can lag a load that has only just finished. Before chasing a
mismatch, press **Get exact row counts** and confirm it persists. For the file side,
**Count records in files** reads the files and counts records rather than lines; it
takes a few seconds per hundred MB and needs the files to still be on the share (in PROD
the CLEO pickup removes them within the hour).

#### *Extra lines* on PB Encounter / HB Encounter — not missing data

**Resolved 2026-10-07.** These were never short. The files are written straight out of
the ODS tables, so a file cannot hold *more* records than its table — and the counts
only ever ran the "wrong" way. The cause is that the extract's counting step counts
**physical lines**, while three `UDF_` columns contain a stray trailing **carriage
return**. One record then spans two lines and gets counted twice.

Proven on DEV 2026-10-06:

| | |
|---|---|
| `MayoClinic_PBEncounter_10062026.txt` physical lines | 397,071 |
| lines starting with a 10-digit EncounterRecordNumber | **395,733** |
| `EPSI_ODS.dbo.T_PB_ENCOUNTER` rows | **395,733** |
| rows with CR/LF in a UDF column | **1,338** = the exact gap |

Columns involved (run [`sql/10_find_embedded_line_breaks.sql`](../sql/10_find_embedded_line_breaks.sql)
for the current picture):

| Env | Table | Column |
|---|---|---|
| DEV | `T_PB_ENCOUNTER` | `UDF_Telemedicine_Mode`, `UDF_Primary_Proc_HAPPA_Flag`, `UDF_TPF_Transplant_Phase` |
| PROD | `T_PB_ENCOUNTER` | `UDF_Primary_Proc_HAPPA_Flag`, `UDF_TPF_Transplant_Phase` |
| PROD | `T_HB_ENCOUNTER` | `UDF_Virtual_Visit` — **1 row**, which is the entire −1 on HB Encounter |

Values look like `'In Person'+CHAR(13)`, `'Video'+CHAR(13)`, or a lone `CHAR(13)`.

**What the console does now.** It answers two questions separately and does not let one
hide the other:

| Question | Answered by | Verdict |
|---|---|---|
| Did every row reach the file? | record count vs table rows | **Short** if they disagree |
| Is the file well formed? | lines vs records | **Rows OK, file split** if they disagree |
| Both good | | **Match** |

A dataset whose records reconcile but whose file contains split records **stays amber**,
with the number of continuation lines shown. Counting the records correctly proves no
data was lost; it does not make the file correct, and letting it go green would retire
the defect while it is still there.

One honest limit: until you press **Count records in files** the comparison is
line-based, and a line-based *Match* is not proof — missing records and split records
can cancel out. The button settles it.

#### The count file sent to Strata has the same bug

The *Declared to Strata* column is the figure from
`MayoClinic_filecounts_MMddyyyy.txt` — the file Strata receives alongside the data. The
extract builds it in the *Create Load Size and Count File* step with:

```powershell
$count = 0
while ($reader.ReadLine() -ne $null) { $count++ }
$lines = $count - 1
```

That is a **physical line count**, so it inherits the split-record problem exactly. On
PROD 2026-10-07 the count file declared:

| File | Declared | Actually contains | Overstated by |
|---|---|---|---|
| `MayoClinic_PBEncounter_10072026.txt` | 379,468 | 378,741 | **+727** |
| `MayoClinic_HBEncounter_10072026.txt` | 351,481 | 351,480 | **+1** |

So Strata is told to expect more records than the file holds. If Strata validates its
load against that number it should have been reporting a shortfall on PB Encounter every
day — worth asking them.

**Cleaning the source columns fixes this too**, and is the better fix: once no field
holds a line break, lines and records are the same number again and the existing
counting step is correct as written. Changing the step to count records instead would
make the declared number right while leaving the file malformed — the wrong half of
the problem to solve.

#### A record to track the fix with

`EncounterRecordNumber` **1190289930** exists on **both** DEV and PROD and is broken in
two columns at once, so one record exercises everything:

| Column | Value | |
|---|---|---|
| `UDF_Primary_Proc_HAPPA_Flag` | `[<CR>]` | 1 break |
| `UDF_TPF_Transplant_Phase` | `[Post<CR><LF>]` | 1 break (CRLF is one terminator) |
| | | **record occupies 3 lines, should be 1** |

Verified against the real file — in `MayoClinic_PBEncounter_10062026.txt` it appears as:

```
1190289930|1184431715|2000613498247|09502992|...|104000061|Post
||||2000613498247|Face to Face|
                                        <- empty third line
```

Check it before and after with
[`sql/12_check_sample_record.sql`](../sql/12_check_sample_record.sql) (read-only; set
`@Encounter` at the top). **`LinesInFile` should go from 3 to 1.**

Other tracers:

| Env | Encounter | Column | Value |
|---|---|---|---|
| PROD | `1102035576` | `T_HB_ENCOUNTER.UDF_Virtual_Visit` | `[<CR>]` — the **only** affected HB row, so HB Encounter's −1 should become 0 |
| PROD | `1188025513` | `UDF_TPF_Transplant_Phase` | `[Post<CR>]` |
| PROD | `1169753669` | `UDF_Primary_Proc_HAPPA_Flag` | `[<CR>]` |
| DEV | `1173161301` | `UDF_Telemedicine_Mode` + `UDF_TPF_Transplant_Phase` | `[In Person<CR>]`, `[<CR>]` |

#### Doing the clean-up

This is a **one-off**, not a recurring scrub. Measured on PROD 2026-10-07: staging holds
zero rows with these characters, and **none** of the 3,454 affected ODS rows appear in
today's staging batch — they are rows the extract no longer refreshes, carrying a value
written on an earlier load. Clean them and they should stay clean.

```powershell
# 1. See what would change. Safe - changes nothing.
pwsh -File sql\Invoke-LineBreakCleanup.ps1 -Server 'ropeps913l01\epsidev'

# 2. Clean DEV. Asks for confirmation; re-scans afterwards.
pwsh -File sql\Invoke-LineBreakCleanup.ps1 -Server 'ropeps913l01\epsidev' -Apply

# 3. Check it took: Counts & reconciliation -> Count records in files.
#    Every dataset should read Match.

# 4. Same on PROD.
pwsh -File sql\Invoke-LineBreakCleanup.ps1 -Server 'ropeps013L01' -Apply
```

The wrapper brackets the change with a scan either side, so you always see before and
after. It strips CR/LF/TAB, trims the space the CR leaves behind, and turns a value that
was *only* whitespace into NULL.

Prefer to drive it yourself? [`sql/11_fix_embedded_line_breaks.sql`](../sql/11_fix_embedded_line_breaks.sql)
runs standalone in SSMS and **dry-runs by default** — set `@Apply = 1` at the top to
commit. It only touches the columns in its `@Targets` list, not every string column.

If it ever comes back, that script lists the standing defences worth considering (a
post-load job step, the ODS load packages, or the file writer). Don't do that work
pre-emptively — the reconciliation tab will show **Extra lines** if it recurs.

**Still open:** whether Strata has been mis-reading those split records all along. A
line-based parser would see a truncated record plus a fragment. Worth comparing Strata's
loaded counts against `T_PB_ENCOUNTER` for a recent day.

#### Other standing conditions

| Env | Dataset | What you will see | Meaning |
|---|---|---|---|
| DEV | `Surgical_Time_Data` | *No data*, table 0 rows | **Not a fault.** DEV step 7 *Surgical File Extract* is commented out, so nothing is produced. PROD is fine (596/596). |

### Reading the Extract volume tab

`epsi_staging.dbo.etl_audit_log` on the data instance: one row per staging-package run,
with `rows_extracted`. The tab turns that into one value per dataset per day.

- **Which run counts.** If a day had several runs (a failure, then a re-run), the last
  *successful* one — the load that stuck.
- **Three kinds of day, drawn differently.** A day that loaded data is on the line. A day
  that **succeeded with 0 rows** is a red ✕ on the baseline, and named in the banner. A day
  with **no successful run** is a gap — normal on DEV, which runs ad hoc.
- **Package renames don't break the line.** DEV's packages changed name over time
  (`LoadStagingHBChargeTable` → `…Optimize`, a `…UsingScript` variant, a lower-case
  `optimize`); they're mapped to the dataset they load. Hover a dataset name in the summary
  to see which packages fed it in the range.
- **Typical day** is the median of loading days, so one full reload (DEV pulled 80 million
  PB ICD10Dx rows on 2026-09-09) doesn't pass for normal.
- **Last 7 vs prior 7** compares the averages of the last seven loading days with the seven
  before them, and flags **influx** at +50% or more, **drop** at −50% or worse. It's an
  average on purpose, so a surge shows within a day or two — which also means a one-off
  reload in either window moves it. Read the flag, then the chart.

**The weekly empty run.** On PROD, **every Thursday since 2026-05-28** the extract has
run, every staging package has reported *Succeeded* with **0 rows**, and the job has gone
green. The higher Friday figures suggest the next day picks up the gap, but the Thursday
files still go to Strata header-only. The banner lists these days for whatever range you
pick.

### Adding to an AdHoc list

The ten `AdhocHARList - *.csv` files in the FTP share tell the next extract which
encounters — or encounter/transaction pairs — to re-pull for data correction.

**How they are used:** near the end of each run, step 14 **replaces** the HB and PB
Encounter lists with the missing parents it found, ready for the next day. When Finance
sends extra entries, you **append** them, and the next day's extract picks up everything.
Step 14's next run then replaces the Encounter lists again — by then the extras have been
used. When step 14 finds no missing parents it leaves the files alone, so their contents,
extras included, are re-pulled again the following day.

**In the AdHoc lists tab:**

1. Click the list. You see how many entries it holds, its header, the last 100 entries,
   and a search box.
2. Choose **Append** or **Remove**.
3. Type or paste entries, or upload a file: pipe-delimited with a header, one per line
   with no header, or an `.xlsx` (first sheet; save an old `.xls` as `.xlsx` first).
   Pipe, tab and comma all work as separators — tab is what pasting cells from Excel
   gives you. A header line is optional and the preview says when it skipped one.
4. **Preview.** Nothing is written. Every line is checked:

   | Column | Rule |
   |---|---|
   | `HSP_ACCOUNT_ID` | 10 digits, always |
   | `TX_ID` (HB Charge, HB Payments) | 9 digits, **optional** |
   | `TDL_ID` (PB Charge, PB Payments) | 15 digits, **optional** |

   In the four two-column lists you will often have only the encounters. Paste them one
   per line and each is written `1234567890|` — the pipe kept, the transaction left empty.
   Where you do have the transaction, put it after the encounter:
   `1234567890|111111111` (HB) or `1234567890|222222222222222` (PB). The two kinds can be
   mixed in one paste, and the preview shows every entry exactly as it will be written.

   `1234567890`, `1234567890|` and ` 1234567890 | ` are all the same entry.
   `1234567890|111111111` is a different one — that single transaction. Entries already in
   the list are skipped, and repeats in your input count once.
5. **Append / Remove.** Only once the preview is clean — if any line is invalid, nothing
   is written, because a correction list that half-loads leaves nobody sure which half
   made it. On PROD you type the environment name, as when starting the job.

It **refuses while the extract is running.** The staging packages read the lists early in
the run and step 14 replaces two of them near the end, so anything written mid-run is
either read too late or wiped. Wait for the run to finish.

It never replaces a whole list and never creates a missing file. Appends go at the end,
written the way step 14 writes (ASCII, CRLF, no line ending after the last entry); the
header and existing entries are left exactly as they were. It keeps no backup copy of
the previous file; that was decided deliberately on 2026-10-13. Every append and remove is
recorded in the console's local audit log.

Not the same file as `\\ropeps013L01\ftp\AdhocHARList.csv` in the `ftp` *root*, which
PROD's `Mayo EPSi - Build AdHocHARList File` job writes. Nothing here touches that one.

### Reading the Recon_Current upserts tab

`STRATA_AUDIT.dbo.DailyReconCurrentProcessLog`, one row per dataset per day, written by
the eleven `Daily_Upsert_*` procedures that the extract's *Upsert Process for
Recon_Current Tables* step calls.

**The whole history is kept, and that is deliberate.** Nothing purges the table and it
grows at eleven rows a day — roughly four thousand a year, which is not worth trimming.

**Everything on screen describes the day you selected**, failures included. Pick a clean
day and the failure banner is absent, even though earlier days failed. What stops a bad
day going unnoticed is the **day picker**, which labels it — `2026-09-30 — 3 failed` —
and the line beside it counting how many days have failures. So the panel is never
misleading about the day in view, and a bad day is still one glance away.

Two things would be lost by keeping only the current day:

- **Failures are usually historical.** The three on 2026-09-30 (an
  `FK_T_HB_CPT_RECON_CURRENT_LastChangeEventID` conflict, twice, and a duplicate key on
  `PK_T_HB_ICD10PX_RECON_CURRENT`) appear on no later day. Discard the history and they
  are gone entirely.
- **The update/insert split only means something comparatively.** DEV HB Charge:

  | | updated | inserted |
  |---|---|---|
  | 10/07 | 273,945 | 186,803 |
  | 10/06 | 749 | 340,934 |
  | 09/30 | 78,536 | 234,785 |

  10/06 is almost entirely inserts — consistent with the first run against empty
  `_CURRENT` tables, which is what it was. The same shape appearing on a settled day
  would mean something had re-keyed or rebuilt the dataset, and no single day's figures
  would tell you.

**Source rows are not shown here.** `RowsInSource` is the same number the Counts tab
already gives you — verified identical across all eleven datasets. Repeating it would
invite reading two panels as two independent confirmations when they are one.

**A dataset processing 0 rows is normal where its extract step is off.** Surgical reads
0 every day on DEV for that reason, and the summary line says so. On PROD it would be
worth chasing.

**A day with more than eleven rows is a retry.** 2026-09-30 has fifteen: HB CPT logged
FAILED twice. The summary names any dataset that logged more than once.

### A dataset split into parts

A file can't exceed 1 GB, so a big one is split. From 2026-10-18, HB Charge (about
18 million rows) has gone out as `MayoClinic_HBCharge_10192026_f1.txt` …
`_f6.txt` instead of one `MayoClinic_HBCharge_10192026.txt`. Each part is a complete file
with its own header line, cut at a line boundary.

The console treats the parts as one dataset, for **every** dataset, not just HB Charge:

- It finds the parts in the counts file, the execution log, and on the share, and uses
  them in place of the single file.
- **Declared to Strata** is the sum of the parts' line counts. The counts file lists each
  part on its own line, header excluded. On 10/19 the six parts sum to 17,955,188, exactly
  the table and the staging count.
- **Count records in files** reads every part, skipping each part's header. If any part
  can't be read, the count shows as unknown rather than a short total.
- The dataset shows a **6 parts** tag; hover it for the file range.
- A **missing part** (f1 and f3 present, no f2) is tagged **check parts**. Its records
  are missing, so the dataset also reconciles short.

The scheduled checker (`sql\checker.ps1`) does the same, and both are tested against the
same fixture to agree (T46, T47).

### Where the file counts come from

The *File lines* column prefers `MayoClinic_filecounts_MMddyyyy.txt`. In **PROD** the
CLEO/FTP pickup collects the output files off the share within the hour, taking that
summary with them — so there the console falls back to parsing
`FileSizes_RecordCounts_ExecutionLog.txt`, which is append-only and survives. Same
numbers, different source. If both are missing you get *No data*, which in PROD usually
just means you picked a date whose log entries have aged out.

### "Could not reach the instance"

Check you are on the network/VPN. The control instance is `roeeps914q,57015` — note the
**port**, not the default 1433. The default instance on the same host does not have this
job and will look like everything vanished.

### The counts tab says the share is unreachable

`\\ropeps913L01\FTP\Strata` needs to be reachable from *your* machine, not just from the
server. Try opening it in Explorer first.

---

### The Transferred files tab

Lists what CLEO actually moved on the selected date, from the archive:

| | |
|---|---|
| DEV | `\\mfad.mfroot.org\rchapp\erp\test_groups\stratajazz\archive\clinical` |
| PROD | `\\mfad.mfroot.org\rchapp\erp\prod_groups\stratajazz\archive\clinical` |

This is deliberately **not** the staging folder (`\\ropeps...\FTP\Strata`) the extract
writes to - the pickup empties that within the hour, so it shows nothing most of the
day. The archive is the durable record. The reconciliation tab still reads the staging
folder, for the counts file and record counting.

CLEO renames each file `<original>.yyyyMMdd_HHmmssfff_OUT`; the tab shows the original
name and parses the direction and transfer time out of the suffix. Click a filename to
reveal the archived name.

- **OUT** - sent to Strata.
- **IN** - received *from* Strata, and the ones worth knowing about.
  `ControlFileExtractReport_*` is a CSV giving **RowsInFile / RowsImported /
  RowsRejected per file**; `*_ImportSummary` is an Excel workbook. The control report is
  the authoritative answer to "did Strata accept what we sent".

**DEV's archive is stale** - the most recent transfer there is 2026-07-13, so picking
today's date legitimately shows nothing. That is the folder's state, not a fault.

---

## 3a. Working on production

PROD is `roeeps014q` — a **default** instance. DEV is `roeeps914q,57015` — a **named**
instance. One character apart. The console makes the difference loud:

- a standing red banner naming the instance,
- a red outline on the environment selector,
- and **starting a job requires typing `PROD`** in the confirmation. That check is
  enforced on the server, not just in the browser, so it holds even if someone scripts
  against the API.

PROD runs on an enabled **Daily Schedule at 07:00**. Before restarting anything, check
the *Next scheduled* figure on the hero card — a manual run that overlaps the scheduled
one will be refused by Agent as "already running", but a manual run finishing just
before 07:00 will still be followed by the scheduled one.

---

## 4. Restarting after a failure

**Before you restart, know what the failed step already did.** Restarting at step *N*
re-runs *N* onward; if *N* partially wrote data, you may need to clean up first. The
console does not and cannot know that.

1. Click the failed step and read the error.
2. Decide whether to resume at that step or start over.
3. Use **Restart from step N** on the hero card, or the button inside any step.
4. Confirm the dialog. It restates that steps 1..N−1 will **not** run.

Consequences worth repeating:

- **The STARTED e-mail does not go out** on a partial restart (that is step 1).
  Recipients get a COMPLETED mail with no STARTED mail.
- The run **continues to step 14**, not just the step you picked.
- Agent records the restart as a new run, so history shows two entries for one business
  day. That is normal and the duration of the second one is the restart only.

---

## 5. Stopping a run

**Stopping mid-extract can leave partial data** in staging/ODS and incomplete files on
the share. The console makes you type the job name to confirm.

Agent stops a job only when the *running step* yields. Step 4 can run for hours — a stop
request against it will sit pending until it does. The status will keep saying *Running*
in the meantime; that is Agent's behaviour, not a bug in the console.

---

## 6. Known failures — the shared catalogue

When a step fails, the console matches its error against a catalogue of known failures
and shows, **on the step itself**, what it means and an ordered list of things to try.
You should rarely need to open the *Known failures* tab; the explanation comes to you.

```
 11  Upsert Process for Recon_Current Tables          X Failed
     +- KNOWN ISSUE  The job account could not log in to the target server
     |  A PowerShell step opened a SQL connection and was refused...
     |
     |  THINGS TO TRY, IN ORDER
     |   1. Confirm the account named in the message has a login on that server.
     |   2. For a PowerShell step, check the Agent proxy credential.
     |   3. If the login exists, check it is also a user in the target database.
     |   4. Check whether the service account password changed recently.
     +- [ Add a fix ]
```

Each entry can carry a note underneath it, and one that has actually resolved the
failure can be marked **verified**, so a guess and a known fix are not shown as equals.

The catalogue lives in **`EpsiSsisHost.diag.FailureRule` on the control instance**, so
everyone's console reads the same table and a fix worked out today helps whoever is on
call next week.

| | Catalogue |
|---|---|
| DEV | `roeeps914q,57015` → `EpsiSsisHost.diag.FailureRule` ✓ installed |
| PROD | `roeeps014q` → `EpsiSsisHost` — run the install script when PROD is ready |

Install with [`sql/20_install_diagnostics.sql`](../sql/20_install_diagnostics.sql) —
re-runnable, and it will not overwrite anything you have written. Until it is run on an
environment, that environment's *Known failures* tab says it cannot reach the catalogue
and nothing else in the console is affected.

> `EpsiSsisHost` is scheduled for decommission in early 2027. The console reaches the
> catalogue through the `DiagnosticsServer` / `DiagnosticsDatabase` settings in
> `appsettings.json`, so relocating it then is those two settings plus a re-run of the
> install script against the new home — no code change.

It ships with 11 rules, every one taken from a failure that has actually happened here —
including the two most common:

| Seen (DEV, 90d) | Rule |
|---|---|
| 8 | **Recon load re-run over rows it had already inserted** — `Cannot insert duplicate key row`. Usually a restart-from-step that re-ran *Populate Recon Tables*. |
| 5 | **Orchestrator finished but a child job failed** — the real error is in the child. |
| 4 | **The job account could not log in to the target server** |
| 4 | **The step will not even parse** — a saved syntax error; nothing ran. |

### Seeing what is and is not explained

The **Recent failures** table lists every failure of this job or its child jobs in the
last 90 days, grouped by distinct error with a *Times* column, and filtered by three
clickable chips:

| Chip | Shows |
|---|---|
| **All** | Everything, explained or not |
| **Explained** | Failures a rule already covers, with the rule's title and action beside each |
| **Not yet explained** | The backlog — each one a rule waiting to be written |

The chip counts are *occurrences*; the rows are *distinct errors*, which is why the
headline reads e.g. "44 failures, 16 distinct". The *Times* column reconciles the two.

### Adding a rule

Filter to **Not yet explained** and click **Add rule** on a row - the pattern is
pre-filled with the distinctive part of the real error, stripped of Agent's boilerplate.
Fill in the title, the cause, and the things to try: **one per line**, most likely
first. It takes effect for everyone on the next refresh. You can also add a rule
directly from a failing step that has no rule yet.

**Adding a fix to a rule that already exists** is the more common case - you worked
something out, and the next person should not have to. Click **Add a fix** on the
failing step or on the rule in the catalogue, and the lines are appended to the end of
its list. You are adding to the list, not editing over anyone else's wording.

Write the actions for whoever is on call at 3am:

- Keep the **pattern** to the distinctive sentence. Agent wraps everything in
  boilerplate (`Executed as user: ... Execute Package Utility ...`) that matches
  anything.
- **Priority** decides which rule wins when several match; lower wins. The SSIS
  catch-all sits at 900 so any specific rule beats it.
- Adding needs `INSERT` on `diag.FailureRule` and `diag.FailureSolution`. Without it
  the console says so rather than failing silently.
### What happened to the Action log

Removed. It recorded start/stop actions to a file on *your own machine*, so it could not
answer the one question it existed for — "who restarted it?" — across the team. The run
history's *Invoked by* column answers that properly, and covers runs started from SSMS
and by the schedule too. The file is still written at
`%LOCALAPPDATA%\ExtractConsole\audit.log` for forensics; it just has no tab.

---

## 7. Routine changes

| Change | Where |
|---|---|
| A step is enabled or disabled | `DisabledSteps` in `appsettings.json` |
| An orchestrator step starts a different child job | `ChildJobs` in `appsettings.json` |
| A recon table or file name changes | `ReconTables` in `appsettings.json` |
| An SSIS package is redeployed under a new name | nothing — packages are read from the job's own step definitions |
| Polling is too chatty | `PollSeconds` (minimum 5) |
| Who the checker mails | `$AllConfig` in `sql\checker.ps1`, then rebuild and re-run the generated `.sql` |

`appsettings.json` is re-read on change, but the browser caches the bootstrap response
for the session — reload the page after editing.

---

## 8. The checker — being told instead of looking

Everything above assumes someone opens the console. The checker is the same logic
running without you. It is a **separate Agent job** on the control instance, so it runs
whether or not your laptop is on.

**Job:** `DBA - CLARITY Extract Checker` — every 30 minutes, 07:00–14:00.
Repeating is deliberate: runs have taken 9 minutes, 1h48m and 5h49m, so any single fixed
time either fires mid-run or hours late. It reports each run **once**, so repeating costs
nothing.

### What it looks for

Not "did the job go green" — that question is already answered, and answered wrongly:
on 2026-10-08 PROD finished in 9m42s with every step successful and shipped Strata
header-only files.

| | |
|---|---|
| **Critical** | did not run · the run failed · every table empty · one table empty that had data last time · a file short of its table |
| **Warning** | a run stopped and never restarted · records split across lines · a dataset collapsed to under half its last count |
| **Info** | still running — the data checks are skipped rather than measured half-loaded |

A `RunFailed` mail names the failing step and, where the catalogue (section 6) has a rule
for the error, its title and the first suggested fix. Adding a solution there improves the
mail as well as the tab.

### When it mails you and when it does not

- **Once per run.** The dedupe keys on the run instance, so a problem found at 08:00 is
  not re-sent at 08:30, 09:00 and 09:30.
- **Nothing at all when there is nothing to say.** No daily "all clear".
- **`NoRecentRun` only where a schedule is enabled.** DEV is started by hand with its
  schedules off, so "has not run for 29 hours" there is a fact about the week, not a
  fault.

### Running it by hand

Either form works and both report the same thing — the console's `--check` is the same
logic in C#, kept deliberately identical to the inline script:

```powershell
# from the checker script itself
powershell -File sql\checker.ps1 -EnvironmentName DEV -DryRun
powershell -File sql\checker.ps1 -EnvironmentName DEV -Force   # report again

# or from the console you already have
ExtractConsole.exe --check --env DEV --dry-run
```

`-DryRun` changes nothing and mails nobody. `-Force` reports again on a run already
reported.

### If the checker's own job goes red

That is the checker breaking, not the extract. It exits **0** when it merely *finds*
something — the mail is the alert — so a red job means one of:

The job only goes red when the **checker itself** breaks — finding a problem leaves it
green and sends the mail. So a red checker means something like: an instance was
unreachable, the proxy lost a permission, or `diag` is missing (`22_` not run here).
The step's own output in Job Activity Monitor says which.

**Nothing is installed anywhere.** The script lives inside the job step. To change it,
edit `sql\checker.ps1`, run `sql\Build-CheckerJobScript.ps1`, and re-run the generated
`.sql` for that instance — **do not edit the step in SSMS**, the next build overwrites
it.

Check `diag.CheckRun.NotifyError` for mail failures specifically — a send that fails is
recorded rather than swallowed, so the run still shows up in history:

```sql
SELECT TOP 10 * FROM EpsiSsisHost.diag.CheckRun ORDER BY CheckRunId DESC;
```

---

## 9. Checks nothing does

Be clear about the edges:

- Neither the console nor the checker verifies the *contents* of the output files beyond
  record counts and the record-start pattern. A file full of the right number of wrong
  rows passes.
- Neither watches CLARITY or the BigQuery source.
- The checker mails; it does not raise a ticket. That is the sibling project,
  [`SQL-agent-capture-failures-log-into-servicenow`](../../SQL-agent-capture-failures-log-into-servicenow),
  which is installed on DEV but **disabled** pending ServiceNow endpoint details.
- The console itself does not run when your machine is off. The checker does — that is
  the whole point of it being a separate Agent job rather than a tab.
