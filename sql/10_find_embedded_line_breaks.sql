/* =============================================================================
   Finds embedded CR / LF / TAB in the ODS tables the extract writes files from.

   WHY THIS MATTERS
   The flat files are written straight out of these tables, one record per row.
   A CHAR(13) or CHAR(10) inside a field value makes the writer emit a physical
   line break in the middle of a record, so any line-based reader sees one record
   as two. That is what produced the long-standing "file has more rows than the
   table" readings on PB Encounter and HB Encounter - the records were all there,
   the line count was simply not a record count.

   Confirmed on DEV 2026-10-06: MayoClinic_PBEncounter_10062026.txt had 397,071
   lines but exactly 395,733 records, matching EPSI_ODS.dbo.T_PB_ENCOUNTER.

   READ-ONLY. Run on the data instance:
       DEV   ropeps913l01\epsidev
       PROD  ropeps013L01
   ========================================================================== */

SET NOCOUNT ON;

DECLARE @Database sysname = N'EPSI_ODS';

/* Tables that feed an extract file. Add to this list if more are added to the job. */
DECLARE @Tables TABLE (name sysname PRIMARY KEY);
INSERT INTO @Tables (name) VALUES
    (N'T_HB_CHARGE'), (N'T_HB_CPT'), (N'T_HB_ENCOUNTER'), (N'T_HB_ICD10DX'),
    (N'T_HB_ICD10PX'), (N'T_HB_PAYMENTS'), (N'T_PB_CHARGE'), (N'T_PB_ENCOUNTER'),
    (N'T_PB_ICD10DX'), (N'T_PB_PAYMENTS'), (N'T_SURGICAL_TIME_DATA');

IF OBJECT_ID('tempdb..#findings') IS NOT NULL DROP TABLE #findings;
CREATE TABLE #findings
(
    TableName   sysname,
    ColumnName  sysname,
    AffectedRows bigint,
    HasCR       bit,
    HasLF       bit,
    HasTab      bit
);

/* The catalog views have to be read from the target database, not from whichever
   database this script happens to be running in - an unqualified sys.columns
   silently matches objects in the current database instead. */
IF OBJECT_ID('tempdb..#cols') IS NOT NULL DROP TABLE #cols;
CREATE TABLE #cols (TableName sysname, ColumnName sysname);

DECLARE @colSql nvarchar(max) = N'
    SELECT o.name, c.name
    FROM ' + QUOTENAME(@Database) + N'.sys.columns AS c
    JOIN ' + QUOTENAME(@Database) + N'.sys.objects AS o ON o.object_id = c.object_id
    JOIN ' + QUOTENAME(@Database) + N'.sys.schemas AS s ON s.schema_id = o.schema_id
    JOIN ' + QUOTENAME(@Database) + N'.sys.types   AS t ON t.user_type_id = c.user_type_id
    WHERE s.name = ''dbo''
      AND o.type = ''U''
      AND t.name IN (''varchar'', ''nvarchar'', ''char'', ''nchar'');';

INSERT INTO #cols (TableName, ColumnName)
EXEC sp_executesql @colSql;

DECLARE @tbl sysname, @sql nvarchar(max);
DECLARE tables_cur CURSOR LOCAL FAST_FORWARD
    FOR SELECT t.name FROM @Tables AS t WHERE EXISTS (SELECT 1 FROM #cols c WHERE c.TableName = t.name);
OPEN tables_cur;
FETCH NEXT FROM tables_cur INTO @tbl;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'';

    /* One UNION ALL branch per string column. HAVING keeps clean columns out. */
    SELECT @sql = @sql
         + CASE WHEN @sql = N'' THEN N'' ELSE N' UNION ALL ' END
         + N'SELECT ' + QUOTENAME(@tbl, '''') + N', ' + QUOTENAME(c.ColumnName, '''') + N', COUNT_BIG(*),'
         + N' MAX(CASE WHEN ' + QUOTENAME(c.ColumnName) + N' LIKE ''%'' + CHAR(13) + ''%'' THEN 1 ELSE 0 END),'
         + N' MAX(CASE WHEN ' + QUOTENAME(c.ColumnName) + N' LIKE ''%'' + CHAR(10) + ''%'' THEN 1 ELSE 0 END),'
         + N' MAX(CASE WHEN ' + QUOTENAME(c.ColumnName) + N' LIKE ''%'' + CHAR(9)  + ''%'' THEN 1 ELSE 0 END)'
         + N' FROM ' + QUOTENAME(@Database) + N'.dbo.' + QUOTENAME(@tbl)
         + N' WHERE ' + QUOTENAME(c.ColumnName) + N' LIKE ''%'' + CHAR(13) + ''%'''
         + N'    OR ' + QUOTENAME(c.ColumnName) + N' LIKE ''%'' + CHAR(10) + ''%'''
         + N'    OR ' + QUOTENAME(c.ColumnName) + N' LIKE ''%'' + CHAR(9)  + ''%'''
         + N' HAVING COUNT_BIG(*) > 0'
    FROM #cols AS c
    WHERE c.TableName = @tbl;

    IF @sql <> N''
    BEGIN
        INSERT INTO #findings (TableName, ColumnName, AffectedRows, HasCR, HasLF, HasTab)
        EXEC sp_executesql @sql;
    END

    FETCH NEXT FROM tables_cur INTO @tbl;
END

CLOSE tables_cur;
DEALLOCATE tables_cur;
DROP TABLE #cols;

/* ---- 1. Columns needing attention ------------------------------------- */
SELECT  TableName,
        ColumnName,
        AffectedRows,
        Characters =
              CASE WHEN HasCR  = 1 THEN 'CR '  ELSE '' END
            + CASE WHEN HasLF  = 1 THEN 'LF '  ELSE '' END
            + CASE WHEN HasTab = 1 THEN 'TAB ' ELSE '' END
FROM #findings
ORDER BY TableName, AffectedRows DESC;

/* ---- 2. Extra physical lines each table will produce ------------------- *
   Rows, not breaks: a value holding two CRs splits a record three ways. Treat
   this as the lower bound on the gap between the file's line and record counts. */
SELECT  TableName,
        ColumnsAffected = COUNT(*),
        RowsAffected    = SUM(AffectedRows)
FROM #findings
GROUP BY TableName
ORDER BY TableName;

IF NOT EXISTS (SELECT 1 FROM #findings)
    SELECT 'Clean - no embedded CR, LF or TAB found in any file-producing column.' AS Result;

DROP TABLE #findings;
GO
