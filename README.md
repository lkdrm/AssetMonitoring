# Asset Monitoring Platform

![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Architecture](https://img.shields.io/badge/architecture-modular%20monolith-blue)
![Current milestone](https://img.shields.io/badge/current-Device%20Catalog%20API-yellow)

> [!WARNING]
> This project is under active development. The public API, persistence model, and architecture may change while the first vertical slices are being implemented.

## Overview

Asset Monitoring Platform is an IoT backend for registering embedded devices, collecting telemetry, detecting abnormal conditions, and notifying administrators.

The first MVP simulates a warehouse with ten devices that report:

- temperature;
- humidity;
- door state;
- light state;
- heartbeat and connectivity status.

The project is also a practical environment for learning ASP.NET Core, EF Core, domain modelling, asynchronous processing, testing, and distributed-system patterns.

## Architecture

The MVP is implemented as a modular monolith. Each module owns its business logic and will later be extractable only when a real scaling or deployment need appears.

```text
AssetMonitoring.Api
├── AssetMonitoring.Modules.DeviceManagement
├── AssetMonitoring.Modules.Telemetry
├── AssetMonitoring.Modules.Alerting
└── AssetMonitoring.Modules.Notifications

AssetMonitoring.DeviceSimulator          planned separate process
```

| Project | Responsibility |
|---|---|
| `AssetMonitoring.Api` | ASP.NET Core host, dependency injection, HTTP endpoints, and composition root |
| `AssetMonitoring.Modules.DeviceManagement` | Device registration, metadata, capabilities, lifecycle, catalog import, and connectivity |
| `AssetMonitoring.Modules.Telemetry` | Telemetry ingestion, normalization, validation, current values, and history |
| `AssetMonitoring.Modules.Alerting` | Threshold, scheduled-state, connectivity, and data-availability alerts |
| `AssetMonitoring.Modules.Notifications` | Notification delivery, retry, and delivery history |
| `AssetMonitoring.DeviceSimulator` | Planned simulation of warehouse devices and telemetry |

## Current Device Catalog flow

```text
device-catalog.json
→ JsonDeviceCatalogReader
→ DeviceCatalogDocument
→ DeviceCatalogValidator
→ DeviceCatalogLoadResult
→ DeviceCatalogController                 in progress
```

The catalog currently contains ten simulated warehouse devices with different hardware models, firmware versions, locations, and capabilities.

## Roadmap

![Current slice](https://img.shields.io/badge/Slice%201-Device%20Catalog%20and%20SQL-yellow)

| Status | Milestone | Details |
|:---:|---|---|
| ✅ | Solution structure | Separate `.NET 10` API and module projects |
| ✅ | Device domain model | Encapsulated identity, metadata, capabilities, registration time, and lifecycle |
| ✅ | Lifecycle behavior | Idempotent retire and restore operations |
| ✅ | Metadata synchronization | Change detection for metadata and capability sets |
| ✅ | Catalog contracts | `DeviceCatalogDocument`, `DeviceCatalogItem`, and reader abstraction |
| ✅ | JSON catalog reader | Async stream deserialization, cancellation, and string enum conversion |
| ✅ | Catalog validation | Device count, required fields, unique codes, and capability validation |
| ✅ | Catalog loader | Application orchestration of reading and validation |
| ✅ | Development catalog | Ten warehouse devices stored in `device-catalog.json` |
| 🚧 | Catalog validation API | Dependency injection and `GET /api/device-catalog/validation` |
| 📋 | SQL Server persistence | EF Core Code First, mappings, constraints, and first migration |
| 📋 | Catalog synchronization | Create, update, skip, retire, and restore devices in one transaction |
| 📋 | Device query API | `GET /api/devices` with device metadata and lifecycle status |
| 📋 | Slice 1 testing | Domain/application unit tests and persistence/API integration tests |
| 📋 | Monitoring activation | Admin activation and idempotent lifecycle transition |
| 📋 | Heartbeat processing | Device sessions, ordering, idempotency, and online/offline status |
| 📋 | Telemetry ingestion | Numeric and state telemetry, validation, persistence, and history |
| 📋 | Alert processing | Threshold, door, light, connectivity, and data-availability rules |
| 📋 | Notifications | Reliable delivery, retry, and notification history |
| 📋 | Device simulator | Ten asynchronous simulated warehouse devices |
| 📋 | Production readiness | Authentication, authorization, observability, Docker, and CI/CD |

Legend: ✅ completed · 🚧 in progress · 📋 planned

## Confirmed business rules

- A catalog must contain exactly ten devices.
- Device codes are unique and compared case-insensitively.
- Every device must declare at least one supported capability.
- Device capabilities are `Temperature`, `Humidity`, `DoorState`, and `LightState`.
- New devices start with the `Registered` lifecycle.
- Device metadata updates do not replace identity, code, or lifecycle state.
- Invalid catalog data produces structured validation errors and must not modify SQL.
- Malformed JSON and inaccessible files are treated as technical failures.
- Devices become active for monitoring only after an explicit activation step.
- Unit and integration tests are added after the complete vertical slice is manually verified.

## Planned API

| Method | Route | Purpose | Status |
|---|---|---|:---:|
| `GET` | `/api/device-catalog/validation` | Read and validate the development device catalog | 🚧 |
| `POST` | `/api/device-catalog/synchronize` | Apply a valid catalog to SQL Server | 📋 |
| `GET` | `/api/devices` | Query registered devices | 📋 |
| `POST` | `/api/devices/{deviceId}/activate` | Activate monitoring for a device | 📋 |
| `POST` | `/api/devices/{deviceId}/heartbeat` | Receive a device heartbeat | 📋 |
| `POST` | `/api/telemetry` | Receive a telemetry message | 📋 |
| `GET` | `/api/alerts` | Query generated alerts | 📋 |

## Development approach

The backend is implemented through small vertical slices:

```text
Business rules
→ Domain behavior
→ Application use case
→ EF Core persistence
→ ASP.NET Core endpoint
→ Manual verification
→ Unit and integration tests
```

The current focus is completing the Device Catalog and SQL persistence slice before beginning heartbeat or telemetry processing.

## Technology direction

- C# and `.NET 10`
- ASP.NET Core Web API
- SQL Server with EF Core Code First
- System.Text.Json
- OpenAPI
- xUnit for unit and integration tests
- Background services, messaging, resilience, observability, and containers in later slices

## Project status

The project is not production-ready yet. The immediate target is a working end-to-end flow that reads and validates the catalog, synchronizes the ten devices into SQL Server, and exposes them through the API.
