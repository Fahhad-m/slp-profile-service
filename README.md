# Rabot SLP service

A small .NET 8 service with two jobs around the Standard Load Profile (SLP) API.

1. **sync** downloads the 15 minute profile (`h1` or `h2`) for a calendar year and stores it in an SQL database. Running it again never creates duplicates and updates what changed.
2. **apply** takes an annual consumption in kWh (for example the one of your own household), computes the expected consumption for every 15 minute interval from the stored profile and saves the result in the database.

## How to run

Requirements: the .NET SDK (version 8 or newer). No database server is needed, SQLite is the default.

```bash
dotnet test                                   # unit tests (SQLite in memory, no network needed)

dotnet run --project src/RabotSlp -- sync --profile h1
dotnet run --project src/RabotSlp -- apply --annual-kwh 3500 --customer my-household --profile h1
```

Options (all optional unless stated, defaults live in `src/RabotSlp/appsettings.json`):

```
sync   [--profile h1|h2] [--year 2026]
apply   --annual-kwh <kWh> [--customer <name>] [--profile h1|h2] [--year 2026] [--basis stored|nominal]
```

The year defaults to the current UTC year. The SQLite file `slp.db` is created in the folder the command is started from. Look at the result with any SQLite tool, for example:

## Assumptions

1. The "current calendar year" is the UTC year, because the API works in UTC. The year can be overridden with `--year`.
2. All timestamps are interval starts in UTC and are stored as UTC. Conversion to local time (including daylight saving) is left to the consumer of the data.
3. A customer is identified by a free text name (`--customer`). The exercise has no customer model.
4. The profile id is taken as given (trimmed, lower case). Unknown ids are reported by the API with a 404, which is shown as an error.
5. Only intervals the API returns are stored. Gaps are not filled with invented values. They are logged as a warning and reported by the sync job.
6. The annual consumption must be greater than 0 and at most 1,000,000,000 kWh, which catches typing mistakes.

## What I found in the API

These observations come from calling the API for 2026 and shaped parts of the design.


## Tests

`dotnet test` runs the unit tests. They use a real SQL database (SQLite in memory), a fake for the API in the job tests and a stubbed HTTP handler for the client tests.

* Sync: first run, running twice (no duplicates, nothing changed), updated values, new intervals, removed intervals, a failing request in the middle of a year leaves the data untouched, an empty answer is refused, profiles are independent, gaps are reported, invalid API data is rejected, requests cover the year in calendar months.
* Apply: the intervals add up to the annual consumption, formula of a single interval, nominal basis, rerun replaces the result, customers are independent, a changed profile is picked up, missing sync and unreasonable input are rejected.
* API client: parsing, request format, `Retry-After` handling, giving up, network errors, 404 and 400 handling.
* Command line parsing and the calendar helpers (leap years).

## What I would do next

* Migrations instead of `EnsureCreated`, and bulk insert for the first load if the data grows.
* Run the sync on a schedule and alert on warnings such as gaps.
* A customer table and an API or report on top of the stored consumption.
* An integration test against a local copy of the API.
