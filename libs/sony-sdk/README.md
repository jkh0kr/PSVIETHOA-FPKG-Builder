# Plaintext PKG build toolkit

Profile: `sdk279-plaintext-direct-v3`

## Graphical interface

Run:

```bat
build-gui.bat
```

The GUI can build a PKG, verify an existing PKG, or extract it to an empty folder.
The build options include the PlayGo chunk count (1 through 255; default 100).
Full format and integrity verification is selected by default. Use `Format only`
when the integrity pass is not needed. When a compact patch is selected, verify
and extract automatically use its adjacent `.remastered.pkg` companion.

## Command line

Build with the default compression level (`7`):

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg"
```

Optional arguments:

- compression level from `-4` through `9`;
- `chunks=COUNT` for 1 through 255 PlayGo chunks (default `100`);
- `force` to replace existing output files;
- `keep` to retain intermediate build files;
- `reference=C:\path\to\base.pkg` to build a patch. This also creates
  `<output>.remastered.pkg`.

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg" 9 chunks=32 force keep
```

PowerShell equivalent:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-from-folder.ps1 `
  -SourceFolder C:\path\to\project `
  -OutputPackage C:\path\to\output\game.pkg `
  -ReferencePackage C:\path\to\output\base.pkg `
  -CompressionLevel 9 `
  -ChunkCount 32
```

The source project must contain an exact 96-byte `sce_sys\keystone`. The source
directory is not modified. Build logs are written next to the output package in a
`<package-name>-build-logs` directory.

For APP builds, existing `sce_sys` presentation PNGs are used when their color
mode matches the SDK requirement. A missing or wrong-mode PNG is recovered
from its DDS into a generated folder; the source PNG is never overwritten.
`icon0*`, `pic0*`, and `pic1*` require 8-bit RGB; `pic2*` requires 8-bit RGBA
with the alpha channel preserved. The GP5 maps PNGs only; Publishing Tools
generates the reserved DDS package entries itself. PSAL builds retain their
required RGB `icon0.png` instead.

For a full APP build, the launcher chooses the smallest `attributePub` level
from the uncompressed size of files actually mapped into the GP5, plus a
metadata/alignment allowance. It writes the selection to a generated copy of
`sce_sys/param.json`; the source file is untouched. SDK 2.79 supports levels
0/1/2, and SDK 3.13 also supports level 3 with `kernel.appSizeInGib`.
`kernel.addcontMountLevel` affects the selected limit: value 1 can use lv1
but not lv2's larger limit; value 2 is capped at 97,945,387,008 bytes. If
the estimated package does not fit, the generated param.json lowers the mount
level only as far as needed (2 to 1 or 0; 1 to 0) and logs a warning. This
changes an application setting in the output package, but not the source file.
The estimate is a preflight choice, not a guarantee of final PKG size. Builds
against a reference PKG retain the source `attributePub` because patch size
alone does not determine the remastered package size.

Use the GUI's temporary-folder field or PowerShell's `-TemporaryDirectory` option
when temporary data must be placed on another drive.

The profile is diagnostic plaintext/unsigned output, not an installable console image.
