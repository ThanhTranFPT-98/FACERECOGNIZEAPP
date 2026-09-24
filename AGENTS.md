# Agent instructions

Read these before changing anything in this repository:

1. `PROJECT_OVERVIEW.md` — purpose, architecture, workflows, data model, API, constraints (section 2A), site profiles (2B).
2. `PROJECT_OVERVIEW.md` section 15 **Bug log** — search it for your error text before debugging from scratch.
3. `PROJECT_OVERVIEW.md` section 16 **Change request log** — the customer's current requirements.

Rules:

- The product serves many customers. Put customer-specific behaviour in configuration (site profile), never in `if customer == ...` code. New features are off by default and can be switched per lane where it makes sense.
- Keep the chain *evidence → authorisation → command → confirmed passage* separate. Never let a camera/AI result open a barrier directly; go through `ParkingService.Observe` and `Open`.
- Entity changes need EF migrations for both `ParkingDb` (SQLite) and `ParkingSqlServerDb`.
- When you fix a bug, add a `BUG-XXX` entry. When you implement a requirement, update its `CR-XXX` entry and the constraint status in section 2A.
- Record what you verified and what you assumed. Don't mark something done without running the tests in section 14.
- Never commit databases, `App_Data/`, evidence images, model weights, credentials or customer documents.
