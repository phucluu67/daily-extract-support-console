/* =============================================================================
   Shows one encounter's UDF values with control characters made visible, so a
   single record can be tracked through the clean-up.

   Run before and after 11_fix_embedded_line_breaks.sql. READ-ONLY.

   Set @Encounter below. Suggested tracer: 1190289930 - it exists on BOTH DEV
   and PROD and is broken in two columns at once, so one record exercises
   everything.

       DEV   ropeps913l01\epsidev
       PROD  ropeps013L01
   ========================================================================== */

SET NOCOUNT ON;

DECLARE @Encounter varchar(20) = '1190289930';

DECLARE @cr char(1) = CHAR(13), @lf char(1) = CHAR(10), @tab char(1) = CHAR(9);

/* -----------------------------------------------------------------------------
   Counting line breaks, not control characters.

   CRLF is TWO characters but only ONE line terminator - StreamReader.ReadLine,
   which is what both the file writer's consumer and the extract's counting step
   use, stops once on it. So normalise CRLF to a single CR first, then count.
   Simply subtracting lengths would report 'Post'+CRLF as two breaks.

   The + '|' sentinel stops LEN() from ignoring trailing spaces.
   -------------------------------------------------------------------------- */

WITH pb AS
(
    SELECT  EncounterRecordNumber,
            tele  = ISNULL(UDF_Telemedicine_Mode, ''),
            happa = ISNULL(UDF_Primary_Proc_HAPPA_Flag, ''),
            tpf   = ISNULL(UDF_TPF_Transplant_Phase, ''),
            rawTele  = UDF_Telemedicine_Mode,
            rawHappa = UDF_Primary_Proc_HAPPA_Flag,
            rawTpf   = UDF_TPF_Transplant_Phase
    FROM EPSI_ODS.dbo.T_PB_ENCOUNTER
    WHERE EncounterRecordNumber = @Encounter
),
pbBreaks AS
(
    SELECT  pb.*,
            b1 = LEN(REPLACE(tele,  @cr + @lf, @cr) + '|')
               - LEN(REPLACE(REPLACE(REPLACE(tele,  @cr + @lf, @cr), @cr, ''), @lf, '') + '|'),
            b2 = LEN(REPLACE(happa, @cr + @lf, @cr) + '|')
               - LEN(REPLACE(REPLACE(REPLACE(happa, @cr + @lf, @cr), @cr, ''), @lf, '') + '|'),
            b3 = LEN(REPLACE(tpf,   @cr + @lf, @cr) + '|')
               - LEN(REPLACE(REPLACE(REPLACE(tpf,   @cr + @lf, @cr), @cr, ''), @lf, '') + '|')
    FROM pb
)
SELECT  Source = 'PB',
        EncounterRecordNumber,
        UDF_Telemedicine_Mode =
            '[' + REPLACE(REPLACE(REPLACE(ISNULL(rawTele, '<NULL>'), @cr, '<CR>'), @lf, '<LF>'), @tab, '<TAB>') + ']',
        UDF_Primary_Proc_HAPPA_Flag =
            '[' + REPLACE(REPLACE(REPLACE(ISNULL(rawHappa, '<NULL>'), @cr, '<CR>'), @lf, '<LF>'), @tab, '<TAB>') + ']',
        UDF_TPF_Transplant_Phase =
            '[' + REPLACE(REPLACE(REPLACE(ISNULL(rawTpf, '<NULL>'), @cr, '<CR>'), @lf, '<LF>'), @tab, '<TAB>') + ']',
        LineBreaks    = b1 + b2 + b3,
        LinesInFile   = 1 + b1 + b2 + b3
FROM pbBreaks

UNION ALL

SELECT  'HB',
        EncounterRecordNumber,
        '(n/a on HB)',
        '(n/a on HB)',
        '[' + REPLACE(REPLACE(REPLACE(ISNULL(UDF_Virtual_Visit, '<NULL>'), @cr, '<CR>'), @lf, '<LF>'), @tab, '<TAB>') + ']',
        bv,
        1 + bv
FROM EPSI_ODS.dbo.T_HB_ENCOUNTER
CROSS APPLY (SELECT v = ISNULL(UDF_Virtual_Visit, '')) AS x
CROSS APPLY (SELECT bv = LEN(REPLACE(x.v, @cr + @lf, @cr) + '|')
                       - LEN(REPLACE(REPLACE(REPLACE(x.v, @cr + @lf, @cr), @cr, ''), @lf, '') + '|')) AS y
WHERE EncounterRecordNumber = @Encounter;

/* LinesInFile = 1 means the record now occupies exactly one line in the output
   file, which is the point of the fix. */
GO
