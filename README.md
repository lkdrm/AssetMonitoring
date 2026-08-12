# AssetMonitoring

![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![EF Core](https://img.shields.io/badge/EF%20Core-10.0-512BD4)
![Device Catalog](https://img.shields.io/badge/device%20catalog-complete-success)

A work-in-progress IoT asset monitoring platform built with .NET.

The platform will collect telemetry from warehouse devices, monitor their
availability, detect abnormal conditions, generate alerts, and deliver
real-time notifications.

The project currently follows a modular monolith architecture. Modules can be
separated into independent services later when real scaling or deployment
requirements appear.

## Architecture

| Module | Responsibility | Status |
|---|---|---|
| `AssetMonitoring.Api` | HTTP API and module composition | 🚧 In progress |
| `DeviceManagement` | Devices, lifecycle, capabilities and catalog synchronization | 🚧 In progress |
| `Telemetry` | Telemetry ingestion, normalization and history | ⬜ Planned |
| `Alerting` | Alert rules, incidents and offline detection | ⬜ Planned |
| `Notifications` | Notification delivery and retries | ⬜ Planned |
| `DeviceSimulator` | Simulation of warehouse devices | ⬜ Planned |

## Current data flow

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