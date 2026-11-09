/* Removes the ExtractConsole integration-test fixture. */

USE msdb;
GO

DECLARE @JobName sysname = N'ZZ_ExtractConsole_SelfTest';

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @JobName)
BEGIN
    -- Stop it first; sp_delete_job on a running job leaves the session behind.
    -- Checked rather than wrapped in TRY/CATCH: sp_stop_job on an idle job raises a
    -- severity-16 error that sqlcmd -b still reports even when T-SQL swallows it.
    IF EXISTS (
        SELECT 1
        FROM msdb.dbo.sysjobactivity AS ja
        JOIN msdb.dbo.sysjobs AS j ON j.job_id = ja.job_id
        WHERE j.name = @JobName
          AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
          AND ja.start_execution_date IS NOT NULL
          AND ja.stop_execution_date IS NULL)
    BEGIN
        EXEC msdb.dbo.sp_stop_job @job_name = @JobName;
        WAITFOR DELAY '00:00:03';
    END

    EXEC msdb.dbo.sp_delete_job @job_name = @JobName, @delete_unused_schedule = 1;
    PRINT 'Removed ' + @JobName;
END
ELSE
    PRINT 'Nothing to remove.';
GO
