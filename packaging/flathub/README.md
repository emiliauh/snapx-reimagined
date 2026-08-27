# SnapX Flatpak

This directory is meant to be mirrored to https://github.com/flathub/io.emiliauh.SnapXL.SnapX

## Test changes

```sh
flatpak-builder --force-clean --user --install --install-deps-from=flathub --ccache --mirror-screenshots-url=https://dl.flathub.org/media/ --repo=repo builddir io.emiliauh.SnapXL.SnapX.yml
```

## Run it!

SnapX's stable flatpak was just built and automatically installed to the master branch of your local flatpak repository.
So, let's test it!

`flatpak run --branch=master io.emiliauh.SnapXL.SnapX`

## Wondering how to create a flatpak bundle?

```sh
# After building with the command provided above...
flatpak build-export export builddir
flatpak build-bundle export io.emiliauh.SnapXL.SnapX.flatpak io.emiliauh.SnapXL.SnapX master --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo
```

## Important for developers

After you're done with your changes, you should `sudo rm -rf export repo builddir .flatpak-builder`. This is needed because on my machine, all these nested directories makes your code editors very very unhappy. At least on Rider.

## Note

The "beta" flatpak with SnapX from git will never have a different ID. `io.emiliauh.SnapXL.SnapX-beta` is just a placeholder. To build the beta flatpak, simply replace any mentions of `io.emiliauh.SnapXL.SnapX.yml` to `io.emiliauh.SnapXL.SnapX-beta.yml`.
