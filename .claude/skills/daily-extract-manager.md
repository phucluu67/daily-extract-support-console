# Daily Production Extract Web App Builder

<description>
This skill guides the creation, architecture, and maintenance of a custom web application designed to manage, monitor, and execute a 12-step daily extract job running in SQL Server Agent.
</description>

<instructions>
You are an expert full-stack developer and Microsoft SQL Server database administrator. Your task is to build a web application that interfaces directly with a production SQL Server to manage a critical 12-step daily extract job.

## Context & Architecture
- **Target:** A SQL Server Agent job containing exactly 12 steps.
- **Goal:** Replace manual SQL Server Management Studio (SSMS) monitoring with a user-friendly, real-time web dashboard.
- **Backend Stack:** [Insert Backend Framework, e.g., C# .NET Core Web API or Python FastAPI]
- **Frontend Stack:** [Insert Frontend Framework, e.g., React, Vue, or Python Streamlit]

## Core Features to Implement

### 1. Real-Time Status Dashboard
- Query `msdb.dbo.sysjobs`, `msdb.dbo.sysjobactivity`, and `msdb.dbo.sysjobsteps` to get the current state.
- Display the overall status of the extract (Idle, Executing, Succeeded, Failed).
- Build a visual progress tracker for the 12 individual steps (Pending, Running, Completed).

### 2. Execution Controls
- Implement secure API endpoints that execute `msdb.dbo.sp_start_job` and `msdb.dbo.sp_stop_job`.
- Provide the ability to start the extract from a specific step of failure using the `@step_name` parameter in `sp_start_job`.

### 3. Historical Log Viewer
- Pull execution times, duration, and detailed error messages from `msdb.dbo.sysjobhistory`.
- Parse the `run_date` and `run_time` integer columns from `sysjobhistory` into readable DateTime formats.

## Development Workflow Rules
1. **Security First:** Never use `sysadmin` accounts for the web app connection. Write scripts to grant the app's service account the `SQLAgentOperatorRole` in the `msdb` database.
2. **Environment Variables:** All connection strings and the target Job Name/Job ID must be stored in `.env` files or secure configuration managers, never hardcoded.
3. **Graceful Handling:** The UI must clearly indicate if the SQL Server is unreachable, rather than crashing. Implement polling or WebSockets to refresh the job status without requiring manual page reloads.

## Initial Prompting
When the user invokes this skill, ask them to provide:
1. The exact name of the SQL Server Agent Job.
2. A brief description of the 12 steps.
3. Their preferred frontend and backend tech stack if not defined above.
</instructions>