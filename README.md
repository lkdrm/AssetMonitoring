# AssetMonitoring

![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![EF Core](https://img.shields.io/badge/EF%20Core-10.0-512BD4)
![SQL Server](https://img.shields.io/badge/SQL%20Server-persistence-CC2927)
![Device Catalog](https://img.shields.io/badge/device%20catalog-complete-success)
![Tests](https://img.shields.io/badge/tests-198%20passing-success)

> [!WARNING]
> This project is under active development. Its public API, persistence model,
> and architecture may change while new vertical slices are implemented.

A work-in-progress IoT asset monitoring platform built with .NET.

The platform will collect telemetry from warehouse devices, monitor their
availability, detect abnormal conditions, generate alerts, and deliver
real-time notifications.

This project is also a practical environment for learning ASP.NET Core,
Entity Framework Core, domain modelling, asynchronous processing,
distributed-system patterns, and backend architecture.

## Documentation

- [AssetMonitoring Complete Interview Guide](docs/AssetMonitoring-Complete-Interview-Guide.md) —
  project architecture, implementation decisions, interview questions, correct
  and incorrect examples, and diagrams.

## Architecture

The project currently follows a modular monolith architecture.

Each module owns its business logic and can later be extracted into an
independent service when real scaling or deployment requirements appear.

| Module | Responsibility | Status |
|---|---|:---:|
| `AssetMonitoring.Api` | HTTP API, dependency injection, and module composition | 🚧 |
| `DeviceManagement` | Devices, lifecycle, capabilities, and catalog synchronization | 🚧 |
| `Telemetry` | Telemetry ingestion, normalization, current values, and history | 🚧 |
| `Alerting` | Alert rules, incidents, and offline detection | ⬜ |
| `Notifications` | Notification delivery, retries, and delivery history | ⬜ |
| `DeviceSimulator` | Simulation of warehouse devices and telemetry | ⬜ |

Legend: ✅ completed · 🚧 in progress · ⬜ planned

## Current end-to-end flow

```text
device-catalog.json
        ↓
JsonDeviceCatalogReader
        ↓
DeviceCatalogValidator
        ↓
DeviceCatalogSynchronizationService
        ↓
IDeviceRepository
        ↓
Entity Framework Core
        ↓
SQL Server
```

The current vertical slice reads ten warehouse devices from a JSON catalog,
validates the complete document, synchronizes device state, and persists the
result in SQL Server.

The Telemetry ingestion flow is also operational:

```text
Device telemetry JSON
        ↓
TelemetryController
        ↓
TelemetryRecordingService
        ↓
ITelemetryMeasurementRepository
        ↓
Entity Framework Core
        ↓
telemetry.Measurements
```

## Project structure

```text
AssetMonitoring
├── src
│   ├── AssetMonitoring.Api
│   ├── AssetMonitoring.Modules.DeviceManagement
│   ├── AssetMonitoring.Modules.Telemetry
│   ├── AssetMonitoring.Modules.Alerting
│   └── AssetMonitoring.Modules.Notifications
├── tests
│   ├── AssetMonitoring.Modules.DeviceManagement.Tests
│   └── AssetMonitoring.Modules.Telemetry.Tests
├── Directory.Build.props
├── AssetMonitoring.slnx
└── README.md
```

A separate `AssetMonitoring.DeviceSimulator` project is planned for a later
vertical slice.

## Implemented

### Device domain

- Encapsulated `Device` aggregate
- Domain-generated `Guid` identity
- Unique catalog device code
- Device metadata:
  - name
  - hardware model
  - hardware revision
  - firmware version
  - location
- Supported capabilities:
  - `Temperature`
  - `Humidity`
  - `DoorState`
  - `LightState`
- Device lifecycle:
  - `Registered`
  - `Active`
  - `Retired`
- Idempotent retirement
- Idempotent restoration
- Metadata change detection
- Capability-set comparison
- UTC timestamp validation
- Read-only capability exposure
- Explicit monitoring activation
- Idempotent repeated activation
- Retired devices must be restored before activation
- Server-generated UTC heartbeat timestamps
- Monotonic heartbeat updates that ignore equal or older timestamps
- Heartbeats accepted only from active devices

### Device connectivity

Connectivity is calculated at query time and is not stored in SQL.

- `NeverConnected` — no heartbeat has been recorded
- `Online` — the latest heartbeat is within the configured threshold
- `Offline` — the latest heartbeat is older than the configured threshold
- Configurable offline threshold through `DeviceManagement:Connectivity`
- One server timestamp shared by every device mapped in the same query
- Lifecycle and connectivity remain independent concepts

### Device catalog

- Development JSON catalog containing exactly ten devices
- Asynchronous stream-based JSON reader
- Cancellation token support
- Case-insensitive JSON property handling
- String enum deserialization
- Catalog load result
- Structured validation result
- Structured validation errors

### Catalog validation

- Exact device-count validation
- Required-field validation
- Case-insensitive duplicate device-code detection
- Missing capability detection
- Duplicate capability detection
- Multiple validation errors collected in one result
- Invalid catalogs rejected before persistence

### Catalog synchronization

The synchronization service compares the JSON catalog with the current SQL
state by device code.

It can:

- create devices missing from SQL;
- update changed metadata;
- leave unchanged devices untouched;
- retire devices missing from the JSON catalog;
- restore retired devices that return to the catalog;
- preserve retired devices for historical data;
- save all detected changes with one `SaveChangesAsync` call.

The synchronization result reports:

- `Created`
- `Updated`
- `Unchanged`
- `Retired`
- `Restored`
- `Applied`
- `HasChanges`

`Applied` indicates that the catalog was valid and synchronization completed.

`HasChanges` indicates whether synchronization modified the database.

### SQL persistence

- SQL Server LocalDB for development
- Entity Framework Core 10
- Code First database model
- Dedicated `device_management` schema
- `Devices` table
- Domain-generated device identifiers
- Unique case-insensitive index on device code
- Required metadata columns
- Lifecycle stored as a string
- Nullable retirement timestamp
- Nullable last-heartbeat timestamp
- Capabilities stored as a primitive collection
- Initial EF Core migration
- Device heartbeat migration
- EF Core model snapshot
- Repository abstraction
- Dependency-injection registration

### Telemetry domain and persistence

- `TelemetryMeasurement` domain entity
- Numeric metrics: `Temperature` and `Humidity`
- State metrics: `DoorState` and `LightState`
- Static factories for numeric and state measurements
- Client-generated measurement identifiers for retry deduplication
- UTC measurement timestamps
- Rejection of empty identifiers, `NaN`, infinity, and invalid value kinds
- Humidity range validation from `0` through `100`, inclusive
- Dedicated `telemetry` SQL schema
- `Measurements` table with numeric and state value columns
- Database check constraints for value kind and humidity range
- Composite history index on device, metric, and descending measurement time
- EF Core repository and scoped Dependency Injection registration

### Telemetry ingestion

- API-owned telemetry request DTO
- Application recording request and result contracts
- `TelemetryRecordingService` orchestration
- Sequential duplicate detection through `MeasurementId`
- Application-level duplicate pre-check with the primary key as the final database guard
- Idempotent duplicate response without another database write
- Cancellation propagated through controller, service, repository, and EF Core
- Numeric and state measurements persisted through the same endpoint

### API

Implemented endpoints:

| Method | Route | Purpose | Status |
|---|---|---|:---:|
| `GET` | `/api/device-catalog/validation` | Read and validate the configured catalog | ✅ |
| `POST` | `/api/device-catalog/synchronize` | Synchronize the catalog with SQL Server | ✅ |
| `GET` | `/api/devices` | Query devices from SQL Server | ✅ |
| `GET` | `/api/devices/{code}` | Query a specific device by code | ✅ |
| `POST` | `/api/devices/{deviceId}/activate` | Activate device monitoring | ✅ |
| `POST` | `/api/devices/{deviceId}/heartbeat` | Receive a device heartbeat | ✅ |
| `POST` | `/api/devices/{deviceId}/telemetry` | Receive device telemetry | ✅ |
| `GET` | `/api/alerts` | Query generated alerts | ⬜ |

Catalog validation failures return `400 Bad Request`.

Successful synchronization returns `200 OK`.

Example synchronization response:

```json
{
  "validationResult": {
    "isValid": true,
    "errors": []
  },
  "created": 0,
  "updated": 0,
  "unchanged": 10,
  "retired": 0,
  "restored": 0,
  "applied": true,
  "hasChanges": false
}
```

## Confirmed business rules

- A development catalog must currently contain exactly ten devices.
- Device codes are unique.
- Device codes are compared case-insensitively.
- Every device must contain at least one capability.
- New devices start with the `Registered` lifecycle.
- Device identity and code are not replaced during metadata updates.
- Devices missing from the catalog are retired rather than physically deleted.
- Retired devices remain in SQL for historical data.
- A retired device is restored when its code returns to the catalog.
- Invalid catalog data must not modify SQL.
- Malformed JSON and inaccessible files are treated as technical failures.
- Device online/offline state is calculated from heartbeat data.
- Monitoring activation is separate from catalog registration.
- Registered devices can transition to `Active`.
- Repeated activation is idempotent and does not write to the database.
- Retired devices must be restored before activation.
- Only active devices can report heartbeats.
- Heartbeat timestamps are generated by the server in UTC.
- Equal or older heartbeat timestamps do not replace the latest timestamp.
- Missing, registered, and retired devices do not modify heartbeat state.
- A device without a heartbeat is `NeverConnected`.
- A heartbeat exactly on the offline threshold is still `Online`.
- Connectivity is calculated and is not persisted as database state.
- A telemetry measurement ID is generated by the sender and reused for retries.
- Temperature and humidity require only a numeric value.
- Door and light state require only a boolean state value.
- Humidity must be between `0` and `100`, inclusive.
- Measurement timestamps must be expressed in UTC.
- A repeated measurement ID is handled idempotently without another SQL insert.
- Unique out-of-order measurements are retained as history.
- Telemetry does not create a cross-module SQL foreign key to Device Management.

## Manual verification

The complete Device Catalog flow has been manually verified.

| Scenario | Expected result | Verified |
|---|---|:---:|
| Initial synchronization | `Created = 10` | ✅ |
| Repeated synchronization | `Unchanged = 10`, `HasChanges = false` | ✅ |
| Metadata modification | `Updated = 1` | ✅ |
| New catalog code | `Created = 1` | ✅ |
| Missing catalog code | `Retired = 1` | ✅ |
| Retired code returned | `Restored = 1` | ✅ |
| Retired device history preserved | 11 physical SQL rows, 10 current catalog devices | ✅ |
| Final repeated synchronization | No unnecessary SQL changes | ✅ |

Telemetry ingestion has also been manually verified against SQL Server:

| Scenario | Expected result | Verified |
|---|---|:---:|
| First telemetry request | `201 Created`, `Recorded = true` | ✅ |
| Repeated measurement ID | `200 OK`, `Recorded = false` | ✅ |
| Duplicate persistence check | Exactly one SQL row | ✅ |
| Numeric measurement mapping | `Temperature = 21.5`, state is `NULL` | ✅ |
| Device and UTC timestamp mapping | Route device ID and event time preserved | ✅ |

## Automated verification

The solution is protected by 198 automated xUnit test cases: 134 for Device
Management and 64 for Telemetry. Coverage includes:

- domain invariants and lifecycle behavior;
- catalog validation and JSON loading;
- synchronization, restoration, retirement, and idempotency;
- Entity Framework Core mappings and SQLite persistence;
- dependency-injection registrations and lifetimes;
- API routing, JSON contracts, filtering, and Problem Details responses;
- device activation domain behavior and application orchestration;
- activation API success, idempotency, not-found, and conflict responses;
- heartbeat domain behavior and application orchestration;
- UTC heartbeat persistence through EF Core and SQLite;
- heartbeat API success, not-found, and lifecycle conflict responses;
- connectivity boundary rules and configuration validation;
- online, offline, and never-connected query contracts;
- telemetry domain invariants and numeric/state factories;
- EF Core Telemetry mapping, constraints, indexes, and UTC round trips;
- repository existence, persistence, and cancellation behavior;
- recording-service mapping, validation, and sequential idempotency;
- Telemetry DI lifetimes and resolution;
- controller mapping and HTTP status selection;
- full JSON-to-EF Core persistence integration through an isolated SQLite API host.

Pull requests run restore, Release build, and all tests through GitHub Actions.
The workflow publishes the real TRX totals for passed, failed, and skipped
tests in both the workflow summary and the generated pull request description.

## Database model

The current `device_management.Devices` table contains:

| Column | SQL type | Required |
|---|---|:---:|
| `Id` | `uniqueidentifier` | ✅ |
| `Code` | `nvarchar(50)` | ✅ |
| `Name` | `nvarchar(200)` | ✅ |
| `HardwareModel` | `nvarchar(100)` | ✅ |
| `HardwareRevision` | `nvarchar(50)` | ✅ |
| `FirmwareVersion` | `nvarchar(50)` | ✅ |
| `Location` | `nvarchar(200)` | ✅ |
| `RegisteredAtUtc` | `datetime2` | ✅ |
| `Lifecycle` | `nvarchar(50)` | ✅ |
| `RetiredAtUtc` | `datetime2` | No |
| `LastHeartbeatAtUtc` | `datetime2` | No |
| `Capabilities` | `nvarchar(200)` | ✅ |

The current `telemetry.Measurements` table contains:

| Column | SQL type | Required |
|---|---|:---:|
| `Id` | `uniqueidentifier` | ✅ |
| `DeviceId` | `uniqueidentifier` | ✅ |
| `Metric` | `nvarchar(50)` | ✅ |
| `NumericValue` | `float` | No |
| `StateValue` | `bit` | No |
| `MeasuredAtUtc` | `datetime2` | ✅ |

## Getting started

### Requirements

- .NET 10 SDK
- SQL Server LocalDB or SQL Server
- Entity Framework Core CLI tools

Clone the repository:

```powershell
git clone https://github.com/lkdrm/AssetMonitoring.git
cd AssetMonitoring
```

Restore dependencies:

```powershell
dotnet restore
```

Build the solution:

```powershell
dotnet build
```

Apply the Device Management migration:

```powershell
dotnet ef database update `
  --project src/AssetMonitoring.Modules.DeviceManagement `
  --startup-project src/AssetMonitoring.Api `
  --context DeviceManagementDbContext
```

Apply the Telemetry migration:

```powershell
dotnet ef database update `
  --project src/AssetMonitoring.Modules.Telemetry `
  --startup-project src/AssetMonitoring.Api `
  --context TelemetryDbContext
```

Run the API:

```powershell
dotnet run --project src/AssetMonitoring.Api
```

Synchronize the development catalog:

```http
POST https://localhost:7056/api/device-catalog/synchronize
Accept: application/json
```

Activate a device and record its heartbeat:

```http
POST https://localhost:7056/api/devices/{deviceId}/activate
Accept: application/json

POST https://localhost:7056/api/devices/{deviceId}/heartbeat
Accept: application/json
```

Record a telemetry measurement:

```http
POST https://localhost:7056/api/devices/{deviceId}/telemetry
Content-Type: application/json
Accept: application/json

{
  "measurementId": "11111111-1111-1111-1111-111111111111",
  "metric": "Temperature",
  "numericValue": 21.5,
  "stateValue": null,
  "measuredAtUtc": "2026-08-31T20:00:00Z"
}
```

The request is also available in:

```text
src/AssetMonitoring.Api/AssetMonitoring.Api.http
```

## Development approach

The backend is developed through small vertical slices:

```text
Business rules
        ↓
Domain behavior
        ↓
Application use case
        ↓
Persistence
        ↓
ASP.NET Core endpoint
        ↓
Manual verification
        ↓
Automated tests
```

The current focus is querying Telemetry history and latest values after the
completed ingestion slice is merged.

## Roadmap

| Status | Milestone | Details |
|:---:|---|---|
| ✅ | Solution structure | Separate .NET 10 API and module projects |
| ✅ | Device domain | Metadata, capabilities, lifecycle, retire, restore, and update behavior |
| ✅ | Device catalog reader | Asynchronous JSON deserialization |
| ✅ | Catalog validation | Required fields, duplicate codes, and capability validation |
| ✅ | Catalog synchronization | Create, update, unchanged, retire, and restore operations |
| ✅ | SQL persistence | EF Core configuration, repository, migration, and SQL schema |
| ✅ | Synchronization API | Catalog synchronization through HTTP POST |
| ✅ | Manual end-to-end verification | JSON → API → EF Core → SQL Server |
| ✅ | Device Query API | List devices and retrieve a device by code |
| ✅ | Automated tests | 198 domain, application, persistence, DI, and API test cases |
| ✅ | Continuous integration | Restore, Release build, tests, and generated PR verification report |
| ✅ | Device activation | Explicit and idempotent monitoring activation |
| ✅ | Device heartbeat | Server-timestamped heartbeat recording for active devices |
| ⬜ | API error handling | Problem Details and global exception handling |
| ⬜ | Structured logging | Synchronization and lifecycle events |
| ✅ | Telemetry domain | Numeric and state telemetry measurements |
| ✅ | Telemetry ingestion API | Idempotent device telemetry endpoint |
| ⬜ | Device simulator | Ten asynchronous warehouse devices |
| ✅ | Device connectivity | Calculated never connected, online, and offline states |
| ⬜ | Alerting | Threshold, state, and heartbeat rules |
| ⬜ | Notifications | Notification delivery and retry handling |
| ⬜ | RabbitMQ | Asynchronous communication between modules |
| ⬜ | MediatR | Internal commands and application events |
| ⬜ | Polly | Retry and resilience policies |
| ⬜ | SignalR dashboard | Real-time warehouse visualization and animations |
| ⬜ | Production readiness | Authentication, authorization, observability, and containers |

## Next milestone

Implement Telemetry read models:

- query measurement history by device and metric;
- return the latest measurement by maximum `MeasuredAtUtc`;
- define deterministic ordering for equal timestamps;
- add pagination boundaries for growing time-series history;
- expose focused read-only API endpoints with `AsNoTracking`;
- add query and API integration tests.

## Technology direction

Current technologies:

- C#
- .NET 10
- ASP.NET Core Web API
- Entity Framework Core 10
- SQL Server
- System.Text.Json
- OpenAPI
- XML documentation
- xUnit
- SQLite for isolated integration tests
- GitHub Actions

Planned technologies where they provide real value:

- FluentAssertions
- MediatR
- RabbitMQ
- Polly
- SignalR
- structured logging
- Docker
- GitHub Actions

## Project status

The project is not production-ready yet.

The Device Management and Telemetry ingestion vertical slices are operational
and covered by automated tests. The API manages the device catalog, lifecycle,
heartbeat, and connectivity projection, then accepts numeric and state telemetry
through an idempotent endpoint and persists measurement history in SQL Server.

The next goal is implementing Telemetry history and latest-value queries.
