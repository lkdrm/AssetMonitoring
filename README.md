# AssetMonitoring

![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![EF Core](https://img.shields.io/badge/EF%20Core-10.0-512BD4)
![SQL Server](https://img.shields.io/badge/SQL%20Server-persistence-CC2927)
![Device Catalog](https://img.shields.io/badge/device%20catalog-complete-success)

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

## Architecture

The project currently follows a modular monolith architecture.

Each module owns its business logic and can later be extracted into an
independent service when real scaling or deployment requirements appear.

| Module | Responsibility | Status |
|---|---|:---:|
| `AssetMonitoring.Api` | HTTP API, dependency injection, and module composition | 🚧 |
| `DeviceManagement` | Devices, lifecycle, capabilities, and catalog synchronization | 🚧 |
| `Telemetry` | Telemetry ingestion, normalization, current values, and history | ⬜ |
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

## Project structure

```text
AssetMonitoring
├── src
│   ├── AssetMonitoring.Api
│   ├── AssetMonitoring.Modules.DeviceManagement
│   ├── AssetMonitoring.Modules.Telemetry
│   ├── AssetMonitoring.Modules.Alerting
│   └── AssetMonitoring.Modules.Notifications
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
- Capabilities stored as a primitive collection
- Initial EF Core migration
- EF Core model snapshot
- Repository abstraction
- Dependency-injection registration

### API

Implemented endpoints:

| Method | Route | Purpose | Status |
|---|---|---|:---:|
| `GET` | `/api/device-catalog/validation` | Read and validate the configured catalog | ✅ |
| `POST` | `/api/device-catalog/synchronize` | Synchronize the catalog with SQL Server | ✅ |
| `GET` | `/api/devices` | Query devices from SQL Server | ✅ |
| `GET` | `/api/devices/{code}` | Query a specific device by code | ✅ |
| `POST` | `/api/devices/{deviceId}/activate` | Activate device monitoring | ⬜ |
| `POST` | `/api/devices/{deviceId}/heartbeat` | Receive a device heartbeat | ⬜ |
| `POST` | `/api/telemetry` | Receive device telemetry | ⬜ |
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
- Device online/offline state will be calculated from heartbeat data.
- Monitoring activation will be separate from catalog registration.

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
| `Capabilities` | `nvarchar(200)` | ✅ |

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

Run the API:

```powershell
dotnet run --project src/AssetMonitoring.Api
```

Synchronize the development catalog:

```http
POST https://localhost:7056/api/device-catalog/synchronize
Accept: application/json
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

The current focus is completing the Device Management backend before beginning
telemetry processing.

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
| 🚧 | Continuous integration | PR restore and Release build workflow pending first GitHub run |
| ⬜ | Device activation | Explicit monitoring activation |
| ⬜ | API error handling | Problem Details and global exception handling |
| ⬜ | Structured logging | Synchronization and lifecycle events |
| ⬜ | Telemetry domain | Numeric and state telemetry measurements |
| ⬜ | Telemetry ingestion API | Device telemetry and heartbeat endpoints |
| ⬜ | Device simulator | Ten asynchronous warehouse devices |
| ⬜ | Device connectivity | Never connected, online, and offline states |
| ⬜ | Alerting | Threshold, state, and heartbeat rules |
| ⬜ | Notifications | Notification delivery and retry handling |
| ⬜ | RabbitMQ | Asynchronous communication between modules |
| ⬜ | MediatR | Internal commands and application events |
| ⬜ | Polly | Retry and resilience policies |
| ⬜ | SignalR dashboard | Real-time warehouse visualization and animations |
| ⬜ | Automated tests | Unit, integration, and API tests |
| ⬜ | Continuous integration | Restore, build, and test pipeline |
| ⬜ | Production readiness | Authentication, authorization, observability, and containers |

## Next milestone

Implement the Device Management read-side API:

- `GET /api/devices`
- `GET /api/devices/{code}`
- response DTOs
- read-only EF Core queries with `AsNoTracking`
- lifecycle filtering
- consistent not-found responses

After the complete Device Management backend is ready, automated unit,
integration, and API tests will be added.

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

Planned technologies where they provide real value:

- xUnit
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

The first Device Management vertical slice is operational: the API reads and
validates the device catalog, synchronizes its state, stores devices in SQL
Server, and preserves retired device history.

The next goal is exposing devices through a read-only query API.