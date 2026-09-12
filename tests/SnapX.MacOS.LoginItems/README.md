# macOS launch-at-login checks

Run the consent, install-path, and state-transition checks on any platform:

```sh
dotnet run --project tests/SnapX.MacOS.LoginItems -c Release
```

On macOS, add `-- --native-status` to read the current process's
`SMAppService.mainAppService.status` through the same Objective-C ABI used by
SnapX:

```sh
dotnet run --project tests/SnapX.MacOS.LoginItems -c Release -- --native-status
```

The native check is read-only. It never registers, unregisters, or opens System
Settings. `NotFound` is a valid result for a service macOS has never seen; the
test only verifies that the returned value maps to the SDK enum.

For a Native AOT interop check on the current Mac architecture:

```sh
runtime_id="osx-$(uname -m | sed 's/x86_64/x64/')"
dotnet publish tests/SnapX.MacOS.LoginItems -c Release -r "$runtime_id" \
  -p:PublishAot=true -p:PublishTrimmed=true -p:SelfContained=true \
  -o /tmp/snapx-login-item-native
/tmp/snapx-login-item-native/SnapX.MacOS.LoginItems --native-status
```

Interactive registration belongs to the installed SnapX app. Copy it to
`/Applications` or `~/Applications`, launch it normally, and use the explicit
first-launch or Settings buttons. Do not use this test to mutate host login
items.
