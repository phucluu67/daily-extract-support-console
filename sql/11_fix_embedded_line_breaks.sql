/* =============================================================================
   Removes embedded CR / LF / TAB from the ODS columns that feed the extract files.

   Run 10_find_embedded_line_breaks.sql first to see what is affected.

   ---------------------------------------------------------------------------
   READ THIS BEFORE RUNNING
   ---------------------------------------------------------------------------
   * Defaults to a DRY RUN. It changes nothing until you set @Apply = 1.
   * It modifies ODS data. Clear it with whoever owns these tables first.
   * Values seen so far are a stray TRAILING carriage return on an otherwise
     ordinary value - 'In Person' + CHAR(13), 'Video' + CHAR(13), and some rows
     where the whole value is just CHAR(13). Nothing suggests the character is
     meaningful. Confirm that before running with @Apply = 1.

   THIS IS EXPECTED TO BE A ONE-OFF, NOT A RECURRING SCRUB
   Measured on PROD 2026-10-07:
     * EPSI_STAGING.dbo.STG_T_PB_ENCOUNTER holds ZERO rows with CR/LF in these
       columns - the data arriving today is clean.
     * Of the 3,454 affected ODS rows, ZERO appear in today's staging batch.
       They are rows the current extract no longer refreshes, carrying a value
       written on some earlier load.
   So cleaning ODS should stay clean. It is not guaranteed - if those rows are
   ever re-extracted from a source that still emits the character, or another
   column starts doing it, it returns. The console now reports that as
   "Extra lines" on the reconciliation tab, so you will see it rather than
   having to go looking. Re-run 10_find_embedded_line_breaks.sql to confirm.

   Run on the data instance:  DEV ropeps913l01\epsidev   PROD ropeps013L01
   ========================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Apply       bit = 0;          -- <<< 1 to actually update
DECLARE @Database    sysname = N'EPSI_ODS';
DECLARE @BlankToNull bit = 1;          -- a value that was ONLY whitespace becomes NULL

/* Columns to clean. Keep this explicit rather than "every string column": an
   UPDATE over every column of a 400k-row table is a far bigger hammer than the
   problem needs, and the affected columns are stable and few. */
DECLARE @Targets TABLE (TableName sysname, ColumnName sysname, PRIMARY KEY (TableName, ColumnName));
INSERT INTO @Targets (TableName, ColumnName) VALUES
    (N'T_PB_ENCOUNTER', N'UDF_Telemedicine_Mode'),
    (N'T_PB_ENCOUNTER', N'UDF_Primary_Proc_HAPPA_Flag'),
    (N'T_PB_ENCOUNTER', N'UDF_TPF_Transplant_Phase'),
    (N'T_HB_ENCOUNTER', N'UDF_Virtual_Visit');

DECLARE @tbl sysname, @col sysname, @sql nvarchar(max), @affected bigint, @total bigint = 0;

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, ColumnName FROM @Targets;
OPEN cur;
FETCH NEXT FROM cur INTO @tbl, @col;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(QUOTENAME(@Database) + '.dbo.' + QUOTENAME(@tbl)) IS NULL
    BEGIN
        PRINT 'SKIP  ' + @tbl + ' - not present on this instance.';
        FETCH NEXT FROM cur INTO @tbl, @col;
        CONTINUE;
    END

    /* Strip the three characters, then trim: a trailing CR usually leaves a
       trailing space behind once removed. */
    DECLARE @cleaned nvarchar(max) =
        N'LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(' + QUOTENAME(@col)
        + N', CHAR(13), ''''), CHAR(10), ''''), CHAR(9), '''')))';

    DECLARE @dirty nvarchar(max) =
        QUOTENAME(@col) + N' LIKE ''%'' + CHAR(13) + ''%'' OR '
      + QUOTENAME(@col) + N' LIKE ''%'' + CHAR(10) + ''%'' OR '
      + QUOTENAME(@col) + N' LIKE ''%'' + CHAR(9)  + ''%''';

    IF @Apply = 0
    BEGIN
        /* Dry run: show what would change, with the characters made visible. */
        SET @sql = N'
            SELECT TOP (10)
                   TableName = ' + QUOTENAME(@tbl, '''') + N',
                   ColumnName = ' + QUOTENAME(@col, '''') + N',
                   CurrentValue = ''['' + REPLACE(REPLACE(REPLACE(' + QUOTENAME(@col)
                 + N', CHAR(13), ''<CR>''), CHAR(10), ''<LF>''), CHAR(9), ''<TAB>'') + '']'',
                   NewValue = ''['' + ISNULL(' + @cleaned + N', ''<NULL>'') + '']'',
                   RowsLikeThis = COUNT_BIG(*) OVER (PARTITION BY ' + QUOTENAME(@col) + N')
            FROM ' + QUOTENAME(@Database) + N'.dbo.' + QUOTENAME(@tbl) + N'
            WHERE ' + @dirty + N'
            GROUP BY ' + QUOTENAME(@col) + N';';

        EXEC sp_executesql @sql;

        SET @sql = N'SELECT @n = COUNT_BIG(*) FROM ' + QUOTENAME(@Database) + N'.dbo.' + QUOTENAME(@tbl)
                 + N' WHERE ' + @dirty + N';';
        EXEC sp_executesql @sql, N'@n bigint OUTPUT', @n = @affected OUTPUT;

        PRINT 'DRY RUN  ' + @tbl + '.' + @col + ' - would update '
            + CAST(ISNULL(@affected, 0) AS varchar(20)) + ' row(s).';
    END
    ELSE
    BEGIN
        SET @sql = N'
            UPDATE ' + QUOTENAME(@Database) + N'.dbo.' + QUOTENAME(@tbl) + N'
            SET ' + QUOTENAME(@col) + N' = '
                  + CASE WHEN @BlankToNull = 1
                         THEN N'NULLIF(' + @cleaned + N', '''')'
                         ELSE @cleaned END + N'
            WHERE ' + @dirty + N';';

        EXEC sp_executesql @sql;
        SET @affected = @@ROWCOUNT;
        PRINT 'UPDATED  ' + @tbl + '.' + @col + ' - ' + CAST(@affected AS varchar(20)) + ' row(s).';
    END

    SET @total += ISNULL(@affected, 0);
    FETCH NEXT FROM cur INTO @tbl, @col;
END

CLOSE cur;
DEALLOCATE cur;

PRINT '';
PRINT CASE WHEN @Apply = 1 THEN 'Applied. ' ELSE 'Dry run only - nothing changed. ' END
    + 'Rows in scope: ' + CAST(@total AS varchar(20));
PRINT 'Re-run 10_find_embedded_line_breaks.sql to confirm.';
GO


/* =============================================================================
   RECOMMENDED ORDER OF WORK

   1. Run this script as a DRY RUN on DEV, read the before/after values.
   2. Run it with @Apply = 1 on DEV. Re-run 10_find_embedded_line_breaks.sql.
   3. Re-run the extract on DEV, or just re-check the console's reconciliation
      tab with "Count records in files" - every dataset should read Match.
   4. Repeat on PROD.

   That is expected to be the end of it, for the reasons in the header.

   IF IT COMES BACK
   Only then is a standing defence worth the effort, cheapest first:

   a. POST-LOAD SCRUB - add a T-SQL step to the extract that runs this script
      with @Apply = 1, after the ODS load and before the file-writing steps.
      One step, no package changes.

   b. CLEAN AT THE ODS LOAD (the LoadODS* SSIS packages) - stop the characters
      entering T_*_ENCOUNTER at all. Conceptually the right place; several
      packages to edit.

   c. DEFEND AT THE FILE WRITER (the packages writing MayoClinic_*.txt) - a
      Derived Column stripping CR/LF/TAB from every string column on the way
      out. The only option that also protects columns nobody has noticed yet,
      and the only one that truly guarantees "one record, one line". Most work.

   ---------------------------------------------------------------------------
   ALSO WORTH CHECKING

   The extract has been shipping these split records to Strata for a long time.
   Whether that matters depends on how Strata parses the files: a reader that
   splits on newlines sees a truncated record followed by a fragment, while one
   that is record-aware is unaffected. Compare Strata's loaded counts against
   T_PB_ENCOUNTER for a recent day before assuming it has been harmless.
   ========================================================================== */
