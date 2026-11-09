/* =============================================================================
   Least-privilege grants for the extract console.

   The console connects with Windows integrated auth as the person using it, so
   these are granted to a *person or an AD group*, not to a service account, and
   nothing here needs sysadmin to use the app.

   Run it once per environment, against that environment's pair of instances:

       env    control instance      data instance
       ----   -------------------   ----------------------
       DEV    roeeps914q,57015      ropeps913l01\epsidev
       PROD   roeeps014q            ropeps013L01

   Note PROD is a DEFAULT instance (no port, no instance name) while DEV is a NAMED
   instance on 57015, and the control host names differ by one character.

   Set the Principal below first. An AD group is strongly preferred over named users.

   This uses :setvar, so run it either with sqlcmd:
       sqlcmd -S roeeps914q,57015 -E -C -i 05_grant_permissions.sql
   or in SSMS with Query > SQLCMD Mode turned on. Without SQLCMD mode SSMS reports
   a syntax error on the ':setvar' line.
   ========================================================================== */

:setvar Principal "MFAD\YourAdGroupOrUser"

/* ===========================================================================
   PART 1 - CONTROL INSTANCE  (DEV roeeps914q,57015  |  PROD roeeps014q)
   msdb: read job status and history; start and stop the job.
   ======================================================================== */

USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(Principal)')
    CREATE LOGIN [$(Principal)] FROM WINDOWS;
GO

USE msdb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(Principal)')
    CREATE USER [$(Principal)] FOR LOGIN [$(Principal)];
GO

/* Read-only: see every job's status and history.
   Without this a non-owner sees only jobs they own, and the console's child-job
   panel comes back empty rather than wrong - but empty is still useless. */
ALTER ROLE SQLAgentReaderRole ADD MEMBER [$(Principal)];

/* Start and stop jobs they do not own.
   SQLAgentOperatorRole includes SQLAgentReaderRole and SQLAgentUserRole. It does
   NOT allow editing or deleting jobs, which is exactly the line we want: the
   console can run the extract and can never redefine it.
   Omit this role for a view-only user - every read panel still works. */
ALTER ROLE SQLAgentOperatorRole ADD MEMBER [$(Principal)];
GO

/* ---------------------------------------------------------------------------
   SSIS catalog - the step 12 panel only.
   catalog.executions and catalog.event_messages show a principal only their own
   executions unless they are ssis_admin. The extract runs under the Agent
   service account, so a normal user sees nothing without this.

   This is optional: without it the SSIS tab reports a permissions message and
   the rest of the console is unaffected. ssis_admin is a broad role - grant it
   only if the SSIS drill-down is actually wanted.
   --------------------------------------------------------------------------- */

USE SSISDB;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(Principal)')
    CREATE USER [$(Principal)] FOR LOGIN [$(Principal)];
GO

-- ALTER ROLE ssis_admin ADD MEMBER [$(Principal)];   -- uncomment deliberately
GO


/* ---------------------------------------------------------------------------
   PART 1b - SHARED FAILURE CATALOGUE  (same CONTROL instance)

   SELECT is enough to read the diagnoses shown on a failing step. Add
   INSERT/UPDATE for anyone who should be able to add a rule from the console;
   without them the console says so plainly rather than failing quietly.

   A login on this instance is not a user in EpsiSsisHost - create the user too.
   Run sql/20_install_diagnostics.sql first.
   --------------------------------------------------------------------------- */

USE EpsiSsisHost;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(Principal)')
    CREATE USER [$(Principal)] FOR LOGIN [$(Principal)];
GO

GRANT SELECT ON diag.FailureRule     TO [$(Principal)];
GRANT SELECT ON diag.FailureSolution TO [$(Principal)];

/* Uncomment for people who should be able to add rules and fixes. */
-- GRANT INSERT, UPDATE ON diag.FailureRule     TO [$(Principal)];
-- GRANT INSERT, UPDATE ON diag.FailureSolution TO [$(Principal)];

/* The checker's own history. SELECT is enough to read it; the Agent proxy that
   RUNS the checker also needs INSERT, so grant it the block below instead.
       DEV   MFAD\WA03749        PROD  MFAD\WA03747
   Skip this if 22_install_checker.sql has not been run here. */
IF OBJECT_ID('diag.CheckRun') IS NOT NULL
BEGIN
    EXEC sp_executesql N'
        GRANT SELECT ON diag.CheckRun   TO [$(Principal)];
        GRANT SELECT ON diag.CheckCount TO [$(Principal)];';
    PRINT 'Granted SELECT on the checker tables.';
END
ELSE
    PRINT 'diag.CheckRun not present - skipped (run 22_install_checker.sql if you want the checker).';

-- For the Agent proxy account that runs ExtractConsole.exe --check:
-- GRANT INSERT ON diag.CheckRun   TO [MFAD\WA03749];
-- GRANT INSERT ON diag.CheckCount TO [MFAD\WA03749];
GO

/* ===========================================================================
   PART 2 - DATA INSTANCE  (DEV ropeps913l01\epsidev  |  PROD ropeps013L01)
   EPSI_ODS: read row counts for the reconciliation panel. SELECT only.
   ======================================================================== */

USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(Principal)')
    CREATE LOGIN [$(Principal)] FROM WINDOWS;
GO

USE EPSI_ODS;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(Principal)')
    CREATE USER [$(Principal)] FOR LOGIN [$(Principal)];
GO

/* The console reads sys.dm_db_partition_stats for fast counts and COUNT_BIG(*)
   when the operator asks for exact ones. The first needs VIEW DATABASE STATE,
   the second needs SELECT. Scoped to the recon tables rather than db_datareader
   so this grant cannot read patient data in the wider ODS. */
GRANT VIEW DATABASE STATE TO [$(Principal)];

GRANT SELECT ON dbo.T_HB_CHARGE_RECON          TO [$(Principal)];
GRANT SELECT ON dbo.T_HB_CPT_RECON             TO [$(Principal)];
GRANT SELECT ON dbo.T_HB_ENCOUNTER_RECON       TO [$(Principal)];
GRANT SELECT ON dbo.T_HB_ICD10DX_RECON         TO [$(Principal)];
GRANT SELECT ON dbo.T_HB_ICD10PX_RECON         TO [$(Principal)];
GRANT SELECT ON dbo.T_HB_PAYMENTS_RECON        TO [$(Principal)];
GRANT SELECT ON dbo.T_PB_CHARGE_RECON          TO [$(Principal)];
GRANT SELECT ON dbo.T_PB_ENCOUNTER_RECON       TO [$(Principal)];
GRANT SELECT ON dbo.T_PB_ICD10DX_RECON         TO [$(Principal)];
GRANT SELECT ON dbo.T_PB_PAYMENTS_RECON        TO [$(Principal)];
GRANT SELECT ON dbo.T_SURGICAL_TIME_DATA_RECON TO [$(Principal)];
GO

/* ---------------------------------------------------------------------------
   STRATA_AUDIT on the SAME data instance: the Recon_Current upserts tab.
   SELECT on the one log table, nothing else - STRATA_AUDIT holds a good deal
   more than this.
   --------------------------------------------------------------------------- */

USE STRATA_AUDIT;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(Principal)')
    CREATE USER [$(Principal)] FOR LOGIN [$(Principal)];
GO

GRANT SELECT ON dbo.DailyReconCurrentProcessLog TO [$(Principal)];
GO

/* ---------------------------------------------------------------------------
   epsi_staging on the SAME data instance: the Extract volume tab.
   SELECT on the one log table - nothing that holds staged patient data.
   --------------------------------------------------------------------------- */

USE epsi_staging;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(Principal)')
    CREATE USER [$(Principal)] FOR LOGIN [$(Principal)];
GO

GRANT SELECT ON dbo.etl_audit_log TO [$(Principal)];
GO

/* File share: the console also lists \\ropeps913L01\FTP\Strata. That is an NTFS
   permission, not a SQL one - the principal needs Read on the share. */



/* ===========================================================================
   VERIFY - run as the principal, on the control instance
   ======================================================================== */
/*
USE msdb;
SELECT  IS_ROLEMEMBER('SQLAgentReaderRole')   AS can_read_jobs,    -- expect 1
        IS_ROLEMEMBER('SQLAgentOperatorRole') AS can_start_stop;   -- 1 to act, 0 view-only

SELECT TOP 1 j.name, h.run_status
FROM msdb.dbo.sysjobs AS j
JOIN msdb.dbo.sysjobhistory AS h ON h.job_id = j.job_id AND h.step_id = 0
WHERE j.name = N'Data Extract From CLARITY for STRATAJAZZ'
ORDER BY h.instance_id DESC;                                       -- expect a row
*/
