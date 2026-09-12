# Authenticated upload integration probe

The default probe runs actual Imgur and Dropbox request/response code against a localhost-only HTTP server using synthetic credentials and payloads. Its test transport refuses every destination except the expected provider hosts, then maps those requests to loopback before any network connection. It does not load account settings or contact live providers.

```sh
dotnet run --project tests/SnapX.Upload.Probe -c Release
```

JSON reflection is disabled to catch NativeAOT-incompatible serialization. Coverage includes bearer headers, multipart and binary bodies, album lists/images, authentication errors, refreshed-token retry with the original payload, Dropbox shared-link requests, and success-status propagation. For a native verification build:

```sh
dotnet publish tests/SnapX.Upload.Probe -c Release -r osx-arm64 -p:PublishAot=true -p:PublishTrimmed=true -p:SelfContained=true -o /tmp/snapx-upload-native
/tmp/snapx-upload-native/SnapX.Upload.Probe
```

An explicitly requested live custom-uploader test can use `--live-sxcu /absolute/path/to/config.sxcu`. This loads the definition through SnapX's import helper and uploads one generated 16×16 blue PNG through `CustomImageUploader`. It prints only the result URL and a safe status; it never copies or prints the configuration or credentials. Do not use this mode without authorization for the selected service.

These checks do not establish that a live Imgur/Dropbox account has valid tokens, app approval, scopes, quota, or service-side compatibility. Those require separately authorized account tests.
