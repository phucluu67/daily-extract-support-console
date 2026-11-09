/* =============================================================================
   Failure diagnostics catalogue for the extract console.

   A shared, curated map from "this error text" to "here is what it means and
   what to do". The console matches a failed step's message against these rules
   and shows the result ON the failing step, so the knowledge reaches whoever is
   looking at the failure rather than sitting in a document.

   It lives in SQL rather than a file beside the exe on purpose: everyone's
   console reads the same catalogue, a fix worked out today helps whoever is on
   call next week, and it survives replacing the exe.

   WHERE THIS GOES
   EpsiSsisHost on the CONTROL instance of each environment:

       DEV   roeeps914q,57015   EpsiSsisHost
       PROD  roeeps014q         EpsiSsisHost

   Note: EpsiSsisHost is scheduled for decommission in early 2027. When it goes,
   change the USE below and the DiagnosticsServer / DiagnosticsDatabase settings
   in appsettings.json to the replacement, then re-run this script there - the
   console reaches the catalogue through those two settings, not through a
   hard-coded connection.

   Re-runnable: seed rules are inserted only where that Pattern is not already
   present, so your edits are never overwritten.
   ========================================================================== */

USE EpsiSsisHost;
GO

IF SCHEMA_ID('diag') IS NULL
    EXEC ('CREATE SCHEMA diag;');
GO

IF OBJECT_ID('diag.FailureRule') IS NULL
BEGIN
    CREATE TABLE diag.FailureRule
    (
        RuleId          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_FailureRule PRIMARY KEY,
        IsEnabled       bit NOT NULL CONSTRAINT DF_FailureRule_IsEnabled DEFAULT (1),

        /* What to look for in the step's message. */
        Pattern         nvarchar(450) NOT NULL,
        MatchType       varchar(10)   NOT NULL CONSTRAINT DF_FailureRule_MatchType DEFAULT ('Contains'),

        /* What to tell the operator. */
        Title           nvarchar(200)  NOT NULL,
        Cause           nvarchar(2000) NULL,
        SuggestedAction nvarchar(2000) NULL,

        /* Lowest number wins when several rules match, so a specific rule beats
           a catch-all. */
        Priority        int NOT NULL CONSTRAINT DF_FailureRule_Priority DEFAULT (100),

        AddedBy         sysname   NOT NULL CONSTRAINT DF_FailureRule_AddedBy DEFAULT (SUSER_SNAME()),
        AddedUtc        datetime2 NOT NULL CONSTRAINT DF_FailureRule_AddedUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedBy       sysname   NULL,
        UpdatedUtc      datetime2 NULL,

        CONSTRAINT CK_FailureRule_MatchType CHECK (MatchType IN ('Contains', 'Regex')),
        CONSTRAINT CK_FailureRule_Pattern   CHECK (LEN(LTRIM(RTRIM(Pattern))) > 0)
    );

    CREATE INDEX IX_FailureRule_Enabled ON diag.FailureRule (IsEnabled, Priority);

    PRINT 'Created diag.FailureRule.';
END
ELSE
    PRINT 'diag.FailureRule already exists.';
GO

/* ---------------------------------------------------------------------------
   Seed rules - every one taken from a failure that has actually happened on
   these instances, not invented.

   Inserted only where that Pattern is not already present, so re-running adds
   newly shipped rules without disturbing anything you have written or edited.
   (A seed rule you deliberately DELETE will come back on the next run - disable
   it with IsEnabled = 0 instead.)
   --------------------------------------------------------------------------- */

DECLARE @Seed TABLE
(
    Pattern         nvarchar(450),
    MatchType       varchar(10),
    Priority        int,
    Title           nvarchar(200),
    Cause           nvarchar(2000),
    SuggestedAction nvarchar(2000)
);

INSERT INTO @Seed (Pattern, MatchType, Priority, Title, Cause, SuggestedAction)
VALUES
    (N'One or both child jobs are already running', 'Contains', 10,
     N'Orchestrator refused to start - child job already running',
     N'The orchestrator step checks whether its child jobs are already in flight and aborts rather than running two copies. Usually means a previous run never finished, or someone started the child job by hand.',
     N'Find what is still running: Steps tab, expand the orchestrator step and look at the child job state. Let it finish, or stop it deliberately, then restart the extract from this step. Do not start the child job separately - the orchestrator will refuse again.'),

    (N'One or both child jobs did not succeed', 'Contains', 10,
     N'Orchestrator finished but a child job failed',
     N'The step started its child jobs and waited. At least one came back with a non-success outcome. The outcome codes in the message are SQL Agent values: 0 = failed, 1 = succeeded, 3 = cancelled.',
     N'The real error is in the CHILD job, not here. Expand this step to see which child failed and at which of ITS steps, then fix that. Restarting the extract from this step will re-run both children.'),

    (N'Login failed for user', 'Contains', 20,
     N'The job account could not log in to the target server',
     N'A PowerShell step opened a SQL connection and was refused. The account is whatever the step runs as - the Agent service account (MSA) for a T-SQL step, or the proxy account for a PowerShell step - and it has no login, or no rights, on the server being connected to.',
     N'Read which account and which server the message names. Confirm that account has a login there; the extract spans roeeps914q/roeeps014q, ropeps913l01/ropeps013L01 and UDPCLARITYP, and rights on one do not imply rights on another. See sql/05_grant_permissions.sql for the console''s own requirements.'),

    (N'does not exist. Make sure that the name is entered correctly', 'Contains', 20,
     N'The step pointed at a database that is not on that server',
     N'SQL error 911. Most often the script is connecting to the right database name on the WRONG instance. The extract is split across two servers: jobs and SSISDB on the control instance, EPSI_ODS / EPSI_STAGING / STRATA_AUDIT on the data instance.',
     N'Check the $Server / -ServerInstance in the step against the database it is asking for. STRATA_AUDIT and EPSI_* live on the DATA instance (ropeps913l01\epsidev in DEV, ropeps013L01 in PROD), not on the job server.'),

    (N'Syntax error', 'Contains', 30,
     N'The step will not even parse',
     N'"Unable to start execution of step N (reason: line(NNN): Syntax error)" means SQL Agent could not compile the step body, so nothing ran. Almost always an edit that was saved broken.',
     N'Open that step in SSMS and go to the line number in the message. Check the job history for when it last ran clean, and who changed it (sysjobs.date_modified). Nothing was executed, so there is no partial data to clean up.'),

    (N'A parameter cannot be found that matches parameter name ''TrustServerCertificate''', 'Contains', 30,
     N'PowerShell step used a parameter the host''s module does not have',
     N'The Agent host runs Windows PowerShell 5.1 with an older SqlServer module. Invoke-Sqlcmd there does not accept -TrustServerCertificate, which newer machines do. Code written and tested on a workstation fails on the server.',
     N'Either remove the parameter and set encryption in the connection string, or install a current SqlServer module on the Agent host. Test PowerShell steps against the host''s version, not your own.'),

    (N'Cannot find path', 'Contains', 40,
     N'A file or share the step needs was not reachable',
     N'The step tried to read or write a path that did not resolve - usually the Strata file share (\\ropeps913L01\FTP\Strata or \\ropeps013L01\FTP\Strata), either because the share was briefly unavailable or because the expected file was not produced by an earlier step.',
     N'Check the share is reachable from the Agent host, not just from your workstation - they run as different accounts. If the share is fine, look at whether the file was supposed to be created by an earlier step that silently produced nothing.'),

    (N'The network path was not found', 'Contains', 40,
     N'The file share did not respond',
     N'The Agent host could not reach the UNC path at all.',
     N'Check the share from the Agent host. Transient network or file-server problems usually clear on a retry; restart the extract from the failing step once it is back.'),

    (N'Access to the path', 'Contains', 40,
     N'Permission denied on a file or share',
     N'The account the step runs as does not have rights on that path. PowerShell steps run under the Agent proxy, not under you.',
     N'Check NTFS/share permissions for the proxy account on the Agent host. Being able to open the path yourself proves nothing - you are a different account.'),

    (N'Cannot insert duplicate key row', 'Contains', 15,
     N'Recon load re-run over rows it had already inserted',
     N'"Populate Recon Tables" inserts each output file''s rows into EPSI_ODS..T_*_RECON, keyed partly on SourceFileName. Running it a second time for the same file hits the unique index. The usual trigger is a restart-from-step: the step already completed earlier in the day, and restarting the extract at or before it runs the load again.',
     N'The rows are already there - this is a duplicate load, not lost data. Confirm the count in the recon table for that SourceFileName, then either delete that file''s rows and re-run the step, or restart the extract from the step AFTER this one. Check the Run history tab for an earlier successful run of this step today before deleting anything.'),

    (N'Execute Package Utility', 'Contains', 900,
     N'SSIS package failed',
     N'The step ran an SSIS package and the package reported failure. The text here is the Execute Package Utility wrapper; the real error is in the SSIS catalogue.',
     N'Open the SSIS packages tab, find this package in the current run and click it for the catalogue''s errors and warnings. That is where the actual cause is. This is a catch-all - if a more specific rule fits this failure, add one.');

INSERT INTO diag.FailureRule (Pattern, MatchType, Priority, Title, Cause, SuggestedAction)
SELECT s.Pattern, s.MatchType, s.Priority, s.Title, s.Cause, s.SuggestedAction
FROM @Seed AS s
WHERE NOT EXISTS (SELECT 1 FROM diag.FailureRule AS r WHERE r.Pattern = s.Pattern);

PRINT 'Added ' + CAST(@@ROWCOUNT AS varchar(10)) + ' new seed rule(s); existing rules untouched.';
GO

/* ---------------------------------------------------------------------------
   Grants. Readers need SELECT; anyone who should be able to add a rule from
   the console needs INSERT and UPDATE. Substitute your AD group.

   This is the control instance, so the principal already has a login here for
   msdb - but a login on the instance is not a user in EpsiSsisHost. Create the
   user too.
   --------------------------------------------------------------------------- */
/*
USE EpsiSsisHost;
CREATE USER [MFAD\YourAdGroup] FOR LOGIN [MFAD\YourAdGroup];
GRANT SELECT, INSERT, UPDATE ON diag.FailureRule TO [MFAD\YourAdGroup];
*/

SELECT RuleId, Priority, MatchType, Title, Pattern
FROM diag.FailureRule
ORDER BY Priority, RuleId;
GO
