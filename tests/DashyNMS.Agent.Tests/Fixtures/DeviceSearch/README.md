# Device search fixture shared with mobile

A synthetic LibreNMS `/api/v0/devices` response (`devices.json`) and what
`find_device` must make of it (`cases.json`). DashyNMS/mobile keeps
byte-identical copies in `tests/DashyNMS.Mobile.Shared.Tests/Fixtures/DeviceSearch`
and runs its Intelligence `find_device` against them; its CI fails if its copies
differ from these at the commit it pins. Change both together.

Every name and address is invented: `example.net` and the documentation ranges
192.0.2.0/24 and 198.51.100.0/24. Never put real network data here.

`cases.json` holds:

- `requests`: the only endpoints a text search may read.
- `cases`: device ids in ranked order and the exact total for each query
  (exact, then starts with, then contains, over hostname, sysName, display name
  and IP; ties by hostname, case-insensitive).
- `states`: the shared status word for chosen devices, and the state LibreNMS
  actually reported. Device 11 reports no state: the shared summary says `down`,
  where mobile shows `Unknown`.
- `failures`: the HTTP status of a failed device list and the
  `DeviceSearchFailure` the host search maps it to. The category is the host's
  contract: the MCP tool returns its own sentence per status instead, so the
  shared part is that both fail without data after reading only `requests`.

Devices 1 to 3 repeat the ranking case in `DeviceSearchTests`. The rest add
sysName prefixes, devices added by IP, duplicate display names, disabled, down
and unreported devices, a secret-shaped display name and marker-shaped text.

`SharedSearchFixtureTests` runs every case and status word through both the MCP
`find_device` tool and `HostDeviceSearch`, and every failure through both.

## What this fixture does not prove

It covers an unscoped text search over one device list. These are not compared
across adapters here, and parity on them should not be claimed from it:

- Scope boundaries: group and location scopes (`devicegroups`, `locations` and
  the scoped device reads), ambiguous scope names and the status filter.
- MAC search over ARP, paging past the first page and the verbose record.
- Failures other than an HTTP status on the device list: timeouts, no response,
  admin-only refusals, malformed JSON, cancellation and a retired connection.
  `HostDeviceSearchTests` covers the host side of these; the MCP side has its
  own tests, against different inputs.
- Faults (alerts) and other tools: `find_device` reads no alerts, so this
  fixture says nothing about fault identities or severity.
