"""
Start the API:   python run.py

Why not just `uvicorn app:app`?  On Windows, Python's default event loop (Proactor) cannot be used
by psycopg, the Postgres driver. This file switches to the loop psycopg needs BEFORE the server
starts. On Linux and macOS it behaves exactly like `uvicorn app:app --reload`.
"""
import asyncio
import sys

if sys.platform == "win32":
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

import uvicorn

if __name__ == "__main__":
    # ONE worker: background tasks and the "interrupted run" cleanup assume a single process.
    uvicorn.run("app:app", host="127.0.0.1", port=8000, reload=True)
