# Apple Crash

Dalamud plugin for host-managed banking and Apple Crash rounds: local auditable banking, editable step macros, party STOP cash-outs, and explicit crash review. Trade detection and unverified dice parsing fail closed.

## Install from a custom plugin repository

1. Copy this URL:
   ```
   https://raw.githubusercontent.com/z1AyN/PNX-Apple-Crash/main/repo.json
   ```
2. In game, open `/xlplugins` -> **Settings** -> **Experimental** -> **Custom Plugin Repositories**.
3. Paste the URL, click the `+`, make sure it is enabled, then **Save and Close**.
4. Find **Apple Crash** in the plugin installer and install it.

Updates are picked up automatically from the `releases/latest` asset named `AppleCrash.zip`.

## Install as a dev plugin (local build)

1. Build:
   ```
   dotnet build AppleCrash.Plugin/AppleCrash.Plugin.csproj -c Debug
   ```
2. In game, open `/xlsettings` -> **Experimental**, enable **Dev Mode**.
3. Add this path under **Dev Plugin Locations**:
   ```
   F:\PNX Apple Crash\AppleCrash.Plugin\bin\Debug\AppleCrash.dll
   ```
4. Restart the game (or `/xlplugins` -> Dev Tools) and enable **Apple Crash**.

Dalamud needs `AppleCrash.json` next to the DLL, which the build already produces.

## Releasing

```
dotnet build AppleCrash.Plugin/AppleCrash.Plugin.csproj -c Release
Compress-Archive -Path AppleCrash.Plugin/bin/Release/AppleCrash.dll,AppleCrash.Plugin/bin/Release/AppleCrash.Core.dll,AppleCrash.Plugin/bin/Release/AppleCrash.json -DestinationPath AppleCrash.zip -Force
gh release create v0.1.0 AppleCrash.zip --title "v0.1.0" --notes "..."
```

Bump `<Version>` in `AppleCrash.Plugin/AppleCrash.Plugin.csproj` and `AssemblyVersion` in `repo.json` together.
