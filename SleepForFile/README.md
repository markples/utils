### Workaround for https://github.com/dotnet/runtime/issues/83346
("Failed to write the updated manifest to the resource of file...")

It is not a good idea to overwrite installation files... so don't use this.

- Consider updating SleepForFile.cs to be coherent (e.g., not both a standalone utility
  and `mt.exe`-specific wrapper).
- Build this project.
- In the directory where `mt.exe` lives (was
  `C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64` on my machine),
  rename `mt.exe` and `mt.exe.config` to `mt_.exe` and `mt_.exe.config`.
- Copy `SleepForFile.exe`, `SleepForFile.dll`, and `SleepForFile.runtimeconfig.json` to
  that directory.  Rename `SleepForFile.exe` to `mt.exe`.