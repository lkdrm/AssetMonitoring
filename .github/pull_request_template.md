## Summary

<!-- Briefly explain what changed and why. -->

-

## Change type

<!-- Select all applicable options. -->

- [ ] Feature
- [ ] Bug fix
- [ ] Refactoring
- [ ] Tests
- [ ] Documentation
- [ ] Database or migration
- [ ] CI or tooling

## Affected modules

<!-- Select all modules affected by this pull request. -->

- [ ] API
- [ ] Device Management
- [ ] Telemetry
- [ ] Alerting
- [ ] Notifications
- [ ] Device Simulator
- [ ] Shared infrastructure

## API changes

<!-- Describe new or changed endpoints. Use N/A when not applicable. -->

| Method | Route | Description |
| --- | --- | --- |
|  |  |  |

## Persistence changes

<!-- Select all applicable options. -->

- [ ] No persistence changes
- [ ] EF Core mapping changed
- [ ] Database migration added
- [ ] Existing migration reviewed
- [ ] Database changes were manually verified

Details:

-

## Verification

<!-- Select everything that was completed. -->

- [ ] `dotnet restore AssetMonitoring.slnx` succeeds
- [ ] Release build succeeds locally
- [ ] All automated tests pass locally
- [ ] Changed API endpoints were manually verified
- [ ] Expected error responses were verified
- [ ] SQL data was verified when persistence changed
- [ ] GitHub Actions CI passes

## Quality checklist

- [ ] The pull request contains only related changes
- [ ] Domain entities are not exposed directly through the API
- [ ] Read-only queries use `AsNoTracking`
- [ ] Async operations accept a `CancellationToken`
- [ ] Required XML documentation was added or updated
- [ ] Public API and database changes are documented
- [ ] No secrets, logs, `bin`, `obj`, or local files were committed
- [ ] I reviewed the complete diff before opening the pull request

## Automated change summary

<!-- change-summary:start -->

⏳ Waiting for GitHub Actions to generate the change summary.

<!-- change-summary:end -->

## Automated CI verification

<!-- ci-results:start -->

⏳ Waiting for GitHub Actions to complete the build and tests.

<!-- ci-results:end -->

## Related issue

<!-- Example: Closes #12. Use N/A when there is no related issue. -->

N/A
