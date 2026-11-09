/* =============================================================================
   Disposable SQL Agent job used to prove the console's start / restart-from-step /
   stop plumbing without running the real extract.

   Four steps, each long enough to catch mid-flight:
     1  waits 20s and succeeds
     2  waits 20s and FAILS            <- gives us a failed step to restart from
     3  waits 20s and succeeds, then QUITS REPORTING SUCCESS
     4  unreachable - step 3 ends the job before it, mirroring PROD's
        "Xifin File Extract" sitting after "Extract Complete Email"

   Step 2 mentions a child job only in a comment and step 3 names it for real, so the
   console's child-job placement can be proved to read step bodies (T54).

   Run 20_remove_selftest_job.sql when you are done.
   ========================================================================== */

USE msdb;
GO

DECLARE @JobName sysname = N'ZZ_ExtractConsole_SelfTest';

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @JobName)
    EXEC msdb.dbo.sp_delete_job @job_name = @JobName, @delete_unused_schedule = 1;

EXEC msdb.dbo.sp_add_job
     @job_name = @JobName,
     @enabled = 1,
     @description = N'Temporary fixture for ExtractConsole integration tests. Safe to delete.';

EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 1, @step_name = N'Selftest step one',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'WAITFOR DELAY ''00:00:20''; PRINT ''step one ok'';',
     @on_success_action = 3, @on_fail_action = 2;

EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 2, @step_name = N'Selftest step two - fails',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'-- EXEC msdb.dbo.sp_start_job N''ZZ_ExtractConsole_SelfTest''   (commented out: must NOT count as launching it)
WAITFOR DELAY ''00:00:20''; RAISERROR(''selftest deliberate failure'', 16, 1);',
     @on_success_action = 3, @on_fail_action = 2;

EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 3, @step_name = N'Selftest step three',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'DECLARE @ChildJob sysname = N''ZZ_ExtractConsole_SelfTest'';   -- names a child job, as the real orchestrators do
WAITFOR DELAY ''00:00:20''; PRINT ''step three ok'';',
     @on_success_action = 1, @on_fail_action = 2;

/* Step 3 quits reporting success, so this one is only ever reached by starting the
   job here explicitly. The console must label it rather than call it "not reached". */
EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 4, @step_name = N'Selftest step four - unreachable',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'PRINT ''step four ok'';',
     @on_success_action = 1, @on_fail_action = 2;

/* Steps 5 and 6 exist only to pin the no-op detector. Both are unreachable, which does
   not matter: whether a step is commented out is a property of its body, not of a run. */

-- Genuinely disabled: body wrapped in /* */ with a live tautology guard, like DEV step 7.
EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 5, @step_name = N'Selftest step five - commented out',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'/*
PRINT ''real work that no longer runs'';
*/
-- DISABLED for testing
IF (1 = 1)
BEGIN
    PRINT ''Step temporarily disabled'';
    RETURN;
END',
     @on_success_action = 1, @on_fail_action = 2;

-- Active: the disable block is ITSELF commented out, exactly like PROD step 9. The
-- detector must NOT call this disabled - getting it wrong here was the reported bug.
EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 6, @step_name = N'Selftest step six - re-enabled',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'PRINT ''this step really does run'';

/*
-- DISABLED on 2026-02-03 by someone
IF (1 = 1)
BEGIN
    PRINT ''Step temporarily disabled'';
    RETURN;
END
*/',
     @on_success_action = 1, @on_fail_action = 2;

/* An SSIS step so package discovery has something to find. The package does not exist
   and the step is unreachable, so it never executes - discovery reads the step
   definition, which is the part under test. */
EXEC msdb.dbo.sp_add_jobstep
     @job_name = @JobName, @step_id = 7, @step_name = N'Selftest step seven - ssis',
     @subsystem = N'SSIS',
     @command = N'/ISSERVER "\"\SSISDB\ZZTestFolder\ZZTestProject\ZZTestPackage.dtsx\"" /SERVER "\"(local)\"" /CALLERINFO SQLAGENT /REPORTING E',
     @on_success_action = 1, @on_fail_action = 2;

EXEC msdb.dbo.sp_add_jobserver @job_name = @JobName, @server_name = N'(local)';

/* A one-time schedule at 07:00 tomorrow. One-time rather than daily so it can never
   actually fire during a test run, while still giving the console a real enabled
   schedule with a known future next-run to report. */
DECLARE @Tomorrow int = CONVERT(int, CONVERT(char(8), DATEADD(day, 1, GETDATE()), 112));

EXEC msdb.dbo.sp_add_jobschedule
     @job_name = @JobName,
     @name = N'ZZ_ExtractConsole_SelfTest_Schedule',
     @enabled = 1,
     @freq_type = 1,                      -- one time only
     @active_start_date = @Tomorrow,
     @active_start_time = 70000;

PRINT 'Created ' + @JobName;
GO
