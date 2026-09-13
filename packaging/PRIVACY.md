# SnapX Privacy Policy

Last updated: September 12, 2026

SnapX uses telemetry to help the developers find errors and improve the
application. Telemetry is off by default. SnapX sends telemetry only when you
set the `SNAPX_TELEMETRY` environment variable to `1`.

## Data that telemetry can send

When you enable telemetry, SnapX can send this data:

- Application usage events.
- The SnapX version and executable name.
- The operating system and its version.
- The processor and graphics processor models.
- The total memory and the memory that the application uses.
- The application package type, such as AppImage, Flatpak, Snap, or a portable
  Windows application.
- Error data and stack traces. SnapX removes known personally identifiable
  information from this data before it sends the data.
- An approximate geographic region, such as a country, state, or city.

SnapX does not send your captured images, videos, text, or files as telemetry.
SnapX does not sell telemetry data.

## Definitions

Telemetry is data about the operation and use of an application.

A stack trace is a list of the functions that ran before an error. Developers
use this list to find the cause of an error.

Fingerprinting is the use of data to identify one device or person.

Anonymous data does not include a name or another direct identifier.

## Telemetry services

SnapX can use these services when telemetry is enabled:

- [Aptabase](https://github.com/aptabase/aptabase) receives application usage
  events.
- [Sentry](https://github.com/getsentry/sentry) receives error, stack trace,
  and performance data. SnapX uses Sentry only when a Sentry connection is
  configured.

Read the [Aptabase privacy policy](https://aptabase.io/legal/privacy).

Read the [Sentry security and privacy information](https://sentry.io/security/)
and the [Sentry CCPA information](https://sentry.io/legal/ccpa/1.0.0/).

These network services process your public IP address when your device connects
to them. Review each service policy for information about its data processing
and retention.

## Disable telemetry

Telemetry is off unless `SNAPX_TELEMETRY=1` is in the environment. Remove that
environment variable to keep telemetry off.

You can also disable telemetry in SnapX settings. Or, set this value in the
application configuration file:

```yaml
DisableTelemetry: true
```

The configuration file is in one of these locations:

- Linux: `~/.config/SnapX/ApplicationConfig.yaml`
- Windows: `%USERPROFILE%\Documents\SnapX\ApplicationConfig.yaml`
- macOS: `~/Library/Application Support/SnapX/ApplicationConfig.yaml`

You can set the `DO_NOT_TRACK` environment variable to `1`. SnapX does not send
telemetry when this variable is set.

Windows administrators can set the following registry value to `true`:

`Computer\HKEY_LOCAL_MACHINE\SOFTWARE\SnapXL\SnapX\DisableTelemetry`

SnapX reads managed and local settings in this order:

1. User Group Policy: `HKCU\Policies\{Path}`
2. Computer Group Policy: `HKLM\Policies\{Path}`
3. User registry: `HKCU\{Path}`
4. Computer registry: `HKLM\{Path}`

Restart SnapX after you change a telemetry setting.

## Data removal requests

The telemetry data does not contain a direct user identifier. Therefore, the
developers cannot find the data for one person and cannot remove that data in
response to an individual request.
