/* =============================================================================
   State for the unattended checker (ExtractConsole.exe --check).

   The checker runs on a timer and looks for the things a green job status
   cannot express: the extract not running at all, running but moving no data,
   a recon shortfall, records split across lines, or a collapse in volume.

   Two tables:
     diag.CheckRun    one row per check that found something, so a repeating
                      schedule notifies once per extract run rather than every
                      tick.
     diag.CheckCount  per-dataset row counts from each check, which is what
                      makes "40% below the last good run" answerable at all.

   Run on the CONTROL instance, after 20_ and 21_:
       DEV   roeeps914q,57015
       PROD  roeeps014q

   Re-runnable.
   ========================================================================== */

USE EpsiSsisHost;
GO

IF SCHEMA_ID('diag') IS NULL
BEGIN
    RAISERROR('diag schema missing - run 20_install_diagnostics.sql first.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('diag.CheckRun') IS NULL
BEGIN
    CREATE TABLE diag.CheckRun
    (
        CheckRunId      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CheckRun PRIMARY KEY,
        EnvironmentName nvarchar(50) NOT NULL,

        /* The extract run being reported on. NULL when the finding is that no
           run happened, which is itself the thing worth knowing. */
        RunInstanceId   bigint NULL,
        RunStartedLocal datetime2 NULL,
        RunOutcome      varchar(20) NULL,

        CheckedUtc      datetime2 NOT NULL CONSTRAINT DF_CheckRun_CheckedUtc DEFAULT (SYSUTCDATETIME()),
        CheckedBy       sysname   NOT NULL CONSTRAINT DF_CheckRun_CheckedBy DEFAULT (SUSER_SNAME()),

        CriticalCount   int NOT NULL CONSTRAINT DF_CheckRun_Critical DEFAULT (0),
        WarningCount    int NOT NULL CONSTRAINT DF_CheckRun_Warning  DEFAULT (0),

        /* Whether an e-mail actually went out, so a mail failure does not look
           like "nobody needed telling". */
        Notified        bit NOT NULL CONSTRAINT DF_CheckRun_Notified DEFAULT (0),
        NotifyError     nvarchar(1000) NULL,

        Summary         nvarchar(max) NULL
    );

    CREATE INDEX IX_CheckRun_Env_Instance ON diag.CheckRun (EnvironmentName, RunInstanceId)
        INCLUDE (CriticalCount, WarningCount, Notified);

    PRINT 'Created diag.CheckRun.';
END
ELSE
    PRINT 'diag.CheckRun already exists.';
GO

IF OBJECT_ID('diag.CheckCount') IS NULL
BEGIN
    CREATE TABLE diag.CheckCount
    (
        CheckCountId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CheckCount PRIMARY KEY,
        CheckRunId   int NOT NULL,
        DatasetKey   nvarchar(100) NOT NULL,

        TableRows    bigint NULL,
        FileLines    bigint NULL,
        FileRecords  bigint NULL,

        CONSTRAINT FK_CheckCount_Run FOREIGN KEY (CheckRunId)
            REFERENCES diag.CheckRun (CheckRunId) ON DELETE CASCADE
    );

    CREATE INDEX IX_CheckCount_Dataset ON diag.CheckCount (DatasetKey, CheckCountId);

    PRINT 'Created diag.CheckCount.';
END
ELSE
    PRINT 'diag.CheckCount already exists.';
GO

/* Grant alongside the catalogue. The checker runs as the Agent proxy, so that
   account needs to write here as well as read the rest. */
/*
GRANT SELECT, INSERT ON diag.CheckRun   TO [MFAD\YourAdGroupOrProxy];
GRANT SELECT, INSERT ON diag.CheckCount TO [MFAD\YourAdGroupOrProxy];
*/

SELECT  CheckRuns = (SELECT COUNT(*) FROM diag.CheckRun),
        Counts    = (SELECT COUNT(*) FROM diag.CheckCount);
GO
