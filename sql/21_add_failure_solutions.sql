/* =============================================================================
   Adds diag.FailureSolution - an ordered list of things to try for each known
   failure, replacing the single SuggestedAction blob on diag.FailureRule.

   Why a child table rather than more text in one column: most of these failures
   have several candidate fixes, people add them at different times, and the
   order matters ("check this first"). A list lets someone append what worked
   without editing over somebody else's wording, and lets a solution be marked
   verified once it has actually resolved the failure.

   Run on the CONTROL instance of each environment, AFTER 20_install_diagnostics.sql:
       DEV   roeeps914q,57015
       PROD  roeeps014q

   Re-runnable. Existing SuggestedAction text is migrated into the list before
   the column is dropped, so nothing written there is lost.
   ========================================================================== */

USE EpsiSsisHost;
GO

IF OBJECT_ID('diag.FailureRule') IS NULL
BEGIN
    RAISERROR('diag.FailureRule does not exist - run 20_install_diagnostics.sql first.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('diag.FailureSolution') IS NULL
BEGIN
    CREATE TABLE diag.FailureSolution
    (
        SolutionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_FailureSolution PRIMARY KEY,
        RuleId     int NOT NULL,

        /* Lower first. Gaps are deliberate so a step can be inserted between
           two existing ones without renumbering. */
        Ordinal    int NOT NULL CONSTRAINT DF_FailureSolution_Ordinal DEFAULT (100),

        /* The action, written as an instruction. */
        Solution   nvarchar(400) NOT NULL,

        /* Optional context - why this one, or what to watch out for. */
        Detail     nvarchar(2000) NULL,

        /* Set once someone confirms this actually resolved the failure, so a
           guess and a known fix are not presented as equals. */
        IsVerified bit NOT NULL CONSTRAINT DF_FailureSolution_IsVerified DEFAULT (0),

        AddedBy    sysname   NOT NULL CONSTRAINT DF_FailureSolution_AddedBy DEFAULT (SUSER_SNAME()),
        AddedUtc   datetime2 NOT NULL CONSTRAINT DF_FailureSolution_AddedUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_FailureSolution_Rule FOREIGN KEY (RuleId)
            REFERENCES diag.FailureRule (RuleId) ON DELETE CASCADE,
        CONSTRAINT CK_FailureSolution_Text CHECK (LEN(LTRIM(RTRIM(Solution))) > 0)
    );

    CREATE INDEX IX_FailureSolution_Rule ON diag.FailureSolution (RuleId, Ordinal);

    PRINT 'Created diag.FailureSolution.';
END
ELSE
    PRINT 'diag.FailureSolution already exists.';
GO

/* ---------------------------------------------------------------------------
   Carry across anything already written in SuggestedAction, as the first entry.
   --------------------------------------------------------------------------- */



/* ---------------------------------------------------------------------------
   Additional candidate fixes for the shipped rules, matched on the rule's
   Pattern so this works whatever RuleIds the install produced. Each is inserted
   only if that exact text is not already present against that rule.
   --------------------------------------------------------------------------- */

DECLARE @Seed TABLE (Pattern nvarchar(450), Ordinal int, Solution nvarchar(400), Detail nvarchar(2000));

INSERT INTO @Seed (Pattern, Ordinal, Solution, Detail) VALUES

-- Orchestrator refused to start
(N'One or both child jobs are already running', 20,
 N'Let the running child job finish, then restart the extract from this step.',
 N'Expand this step in the Steps tab to see which child is still going and how long it has been running.'),
(N'One or both child jobs are already running', 30,
 N'If the child is wedged rather than working, stop it deliberately, then restart from this step.',
 N'Stopping mid-child can leave partial staging data. Check what it had loaded before restarting.'),
(N'One or both child jobs are already running', 40,
 N'If nothing is actually running, look for a stale session in msdb.dbo.sysjobactivity.',
 N'An Agent restart can leave an activity row with start_execution_date set and stop_execution_date NULL, which reads as "running".'),

-- Orchestrator child failed
(N'One or both child jobs did not succeed', 20,
 N'Fix the child job first - the real error is in its history, not here.',
 N'Outcome codes in the message are Agent values: 0 failed, 1 succeeded, 3 cancelled.'),
(N'One or both child jobs did not succeed', 30,
 N'Once fixed, run the failed child on its own to confirm, then restart the extract from this step.',
 N'Restarting here re-runs every child this step launches, not just the one that failed.'),

-- Login failed
(N'Login failed for user', 20,
 N'Confirm the account named in the message has a login on the server named in the message.',
 N'The extract spans roeeps914q/roeeps014q, ropeps913l01/ropeps013L01 and UDPCLARITYP. Rights on one imply nothing about another.'),
(N'Login failed for user', 30,
 N'For a PowerShell step, check the Agent proxy credential - the step does not run as you.',
 N'PowerShell steps here run under the PowerShellExecution proxy; T-SQL steps run as the Agent service account.'),
(N'Login failed for user', 40,
 N'If the login exists, check it is also a user in the target database with rights to what the step touches.',
 N'A server login is not a database user. This is the usual cause when the account "obviously" exists.'),
(N'Login failed for user', 50,
 N'Check whether the service account password or MSA registration changed recently.', NULL),

-- Wrong database / server
(N'does not exist. Make sure that the name is entered correctly', 20,
 N'Compare the step''s $Server / -ServerInstance against the database it is asking for.',
 N'EPSI_ODS, EPSI_STAGING and STRATA_AUDIT are on the DATA instance. msdb, SSISDB and EpsiSsisHost are on the CONTROL instance.'),
(N'does not exist. Make sure that the name is entered correctly', 30,
 N'Confirm the database still exists and has not been renamed or taken offline.',
 N'SELECT name, state_desc FROM sys.databases on the instance the step connects to.'),

-- Syntax error
(N'Syntax error', 20,
 N'Open the step in SSMS and go to the line number quoted in the message.', NULL),
(N'Syntax error', 30,
 N'Check who last changed the job and when, then compare against the version in source control.',
 N'SELECT name, date_modified FROM msdb.dbo.sysjobs. The project folder holds the scripts the step should match.'),
(N'Syntax error', 40,
 N'No cleanup is needed - the step never executed, so no partial data was written.', NULL),

-- TrustServerCertificate
(N'A parameter cannot be found that matches parameter name ''TrustServerCertificate''', 20,
 N'Remove the parameter and set encryption in the connection string instead.', NULL),
(N'A parameter cannot be found that matches parameter name ''TrustServerCertificate''', 30,
 N'Or install a current SqlServer module on the Agent host so the parameter exists.',
 N'The host runs Windows PowerShell 5.1 with an older module. Code written on a modern workstation fails there.'),
(N'A parameter cannot be found that matches parameter name ''TrustServerCertificate''', 40,
 N'Test PowerShell steps against the Agent host''s PowerShell version, not your own.', NULL),

-- Cannot find path
(N'Cannot find path', 20,
 N'Check the path from the Agent host, not from your workstation - different account, possibly different mappings.', NULL),
(N'Cannot find path', 30,
 N'Check whether an earlier step was supposed to produce the missing file and silently produced nothing.',
 N'The Counts and reconciliation tab shows whether the expected files exist for that date.'),
(N'Cannot find path', 40,
 N'Check the date stamp in the expected file name matches the run date.',
 N'Files are named MayoClinic_<Dataset>_MMddyyyy.txt. A restart on a later day looks for a different name.'),

-- Network path
(N'The network path was not found', 20,
 N'Retry once the file server is reachable - restart the extract from the failing step.', NULL),
(N'The network path was not found', 30,
 N'Confirm the Agent host can resolve and reach the UNC path.', NULL),

-- Access denied
(N'Access to the path', 20,
 N'Check NTFS and share permissions for the account the step runs as.',
 N'Opening the path yourself proves nothing - PowerShell steps run under the Agent proxy.'),
(N'Access to the path', 30,
 N'Check whether the file is locked by another process, such as an in-flight transfer.', NULL),

-- Duplicate key in recon
(N'Cannot insert duplicate key row', 20,
 N'Confirm the rows are already there before doing anything: count the recon table for that SourceFileName.',
 N'SELECT COUNT(*) FROM EPSI_ODS..T_<X>_RECON WHERE SourceFileName = ''<file>''. This is a duplicate load, not lost data.'),
(N'Cannot insert duplicate key row', 30,
 N'Restart the extract from the step AFTER this one, so the load is not repeated.',
 N'Usually the right answer when the step already completed earlier in the day.'),
(N'Cannot insert duplicate key row', 40,
 N'Or delete that file''s rows from the recon table and re-run this step.',
 N'Only if you need the load redone. Check Run history for an earlier successful run of this step today first.'),

-- SSIS catch-all
(N'Execute Package Utility', 20,
 N'Open the SSIS packages tab, find this package in the run and click it for the catalogue''s errors.',
 N'The Execute Package Utility text here is a wrapper. The real error is in SSISDB.'),
(N'Execute Package Utility', 30,
 N'If a specific cause emerges, add a rule for it so the next person does not have to dig.',
 N'This rule is a catch-all at priority 900; any specific rule you add will win over it.');

INSERT INTO diag.FailureSolution (RuleId, Ordinal, Solution, Detail)
SELECT r.RuleId, s.Ordinal, s.Solution, s.Detail
FROM @Seed AS s
JOIN diag.FailureRule AS r ON r.Pattern = s.Pattern
WHERE NOT EXISTS (
    SELECT 1 FROM diag.FailureSolution AS x
    WHERE x.RuleId = r.RuleId AND x.Solution = s.Solution);

PRINT 'Added ' + CAST(@@ROWCOUNT AS varchar(10)) + ' new solution(s); existing ones untouched.';
GO

/* Dynamic SQL, not a plain IF: the whole batch is compiled before any of it
   runs, so a direct reference to SuggestedAction fails on the second run even
   though the branch is not taken. */
IF COL_LENGTH('diag.FailureRule', 'SuggestedAction') IS NOT NULL
BEGIN
    DECLARE @migrate nvarchar(max) = N'
        INSERT INTO diag.FailureSolution (RuleId, Ordinal, Solution, AddedBy)
        SELECT  r.RuleId, 10, LEFT(r.SuggestedAction, 400), r.AddedBy
        FROM diag.FailureRule AS r
        WHERE r.SuggestedAction IS NOT NULL
          AND LEN(LTRIM(RTRIM(r.SuggestedAction))) > 0
          AND NOT EXISTS (SELECT 1 FROM diag.FailureSolution AS s WHERE s.RuleId = r.RuleId);
        SELECT @n = @@ROWCOUNT;';

    DECLARE @migrated int;
    EXEC sp_executesql @migrate, N'@n int OUTPUT', @n = @migrated OUTPUT;

    PRINT 'Migrated ' + CAST(ISNULL(@migrated, 0) AS varchar(10)) + ' SuggestedAction value(s) into the list.';
END
ELSE
    PRINT 'SuggestedAction already retired - nothing to migrate.';
GO
/* ---------------------------------------------------------------------------
   Retire SuggestedAction now its content lives in the list. Leaving a column
   the app no longer reads is how catalogues start lying.
   --------------------------------------------------------------------------- */

IF COL_LENGTH('diag.FailureRule', 'SuggestedAction') IS NOT NULL
BEGIN
    ALTER TABLE diag.FailureRule DROP COLUMN SuggestedAction;
    PRINT 'Dropped diag.FailureRule.SuggestedAction - superseded by diag.FailureSolution.';
END
GO

/* Grant alongside the rule table. */
/*
GRANT SELECT, INSERT, UPDATE ON diag.FailureSolution TO [MFAD\YourAdGroup];
*/

SELECT  r.RuleId, r.Title, Solutions = COUNT(s.SolutionId)
FROM diag.FailureRule AS r
LEFT JOIN diag.FailureSolution AS s ON s.RuleId = r.RuleId
GROUP BY r.RuleId, r.Title
ORDER BY r.RuleId;
GO
