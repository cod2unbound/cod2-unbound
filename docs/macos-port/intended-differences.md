# Intended legacy differences from upstream

The reference is opencod2 `410342a`. The owner confirmed the six shared fixes
on 2026-11-21. The source check also reports seven other differences below.
The 12 files that differ in all ten include-free configurations account for
120 comparisons. The Mach-O alias differs in both Apple i386 patch modes,
bringing the total to 122. Player effects below come from source inspection.
The compiler controls establish which objects change.

## Shared fixes

| Entry | Source and functions | Upstream | CoD2 Unbound and player impact | Evidence |
| --- | --- | --- | --- | --- |
| Query allocation | [`CDirect3DDevice.c`](../../src/Mac/DirectX_9/CDirect3DDevice.c), `CDirect3DDevice_CreateQuery`, `Q_Release` | If allocation fails, query initialization writes through a null pointer. | Returns `E_OUTOFMEMORY` before initializing the query. A player avoids a crash on this failure path. The release braces and omitted unused `Type` cast also differ in source. | The Linux i386 object matches upstream after stripping debug and reversing only the allocation check. This control retains the braces and cast changes. |
| Clipboard byte | [`cl_keys_mp.c`](../../src/PC/client_mp/cl_keys_mp.c), `Field_PasteClipboard` | A clipboard byte such as `0xe9` reaches `Field_CharEvent` as a negative value on signed-char targets and is rejected as a control character. | Converts the byte to `unsigned char`. Extended characters in the game's encoding survive paste. | Reversing the cast alone reproduces the stripped upstream Linux i386 object. |
| Screenshot queue | [`r_screenshot.c`](../../src/PC/gfx_d3d/r_screenshot.c), `R_ScreenshotCommand`, `R_HasPendingScreenshot`, `R_CapturePendingScreenshotBeforePresent` | One pending slot holds the filename and delay. A second request overwrites the first and resets its delay. | Holds requests in a queue with their own filename and delay. Closely spaced requests can each produce a screenshot. | Reversing the queue alone reproduces the stripped upstream Linux i386 object. |
| Favorite selection | [`ui_main_mp.c`](../../src/PC/ui_mp/ui_main_mp.c), `UI_RunMenuScript` for `deleteFavorite` | Copies the selected row before refreshing the count. A rebuild can reset selection while deletion still uses the old index. | Refreshes first, then reads and bounds-checks selection. If the rebuild clears selection, deletion stops before removing a favorite. | Reversing the call order alone reproduces the stripped upstream Linux i386 object. |
| Fatal exit status | [`mac_main.c`](../../src/Mac/Main/mac_main.c), `Sys_Error`, `Sys_Quit` | Normal cleanup exits with status 0 after a fatal error. | Stores status 1 before fatal cleanup. Launchers, shells and service supervisors can distinguish failure from normal shutdown. Players still get the fatal error message. | Reversing the status change alone reproduces both stripped upstream Linux i386 entry-point objects. These are the client and dedicated compile targets that the ordinary build never reaches. |
| Specularity prototype | [`r_image_load_obj.c`](../../src/PC/gfx_d3d/r_image_load_obj.c), `Image_Load` and its declaration of `Image_BuildSpecularityMap` | Declares the first parameter as `int` at the call site even though the [definition](../../src/PC/gfx_d3d/r_specularityimage.c) takes `float`. | Declares the same `float` parameter type as the definition. The current call passes zero. The Linux i386 build showed no player-visible change. | Source inspection confirms the type mismatch. The stripped Linux i386 object matches upstream. |

## Other differences from the source check

| Entry | Source and functions | Upstream | CoD2 Unbound and player impact | Evidence |
| --- | --- | --- | --- | --- |
| Directory name alias | [`mac_common.c`](../../src/Mac/Main/mac_common.c), `Sys_ListFiles` | Reads `d->d_name` directly. | Reads the same directory entry through a local `name` alias. No legacy behavior change. | The stripped Linux i386 object matches upstream. |
| DPVS cell declaration | [`r_dpvs.c`](../../src/PC/gfx_d3d/r_dpvs.c), `R_AddWorldSurfacesDpvs_impl` | Initializes `cell` in its declaration. | Declares `cell` separately so the native code can select an alternate value. The legacy branch still makes the same call and assignment. No legacy player effect. | The stripped Linux i386 object matches upstream. |
| Vertex color block | [`rb_shade.c`](../../src/PC/gfx_d3d/rb_shade.c), `RB_CopyVerticesWithColorConvert` | Issues the conversion call as a bare statement. | Wraps the same call in an extra block in the legacy branch. No legacy player effect. | The stripped Linux i386 object matches upstream. |
| Depth target predicate | [`rb_state.c`](../../src/PC/gfx_d3d/rb_state.c), `RB_SetRenderTarget` | Places the closing parenthesis on the pointer-comparison line. | Places it after the native-only clauses. The legacy predicate is the same pointer comparison. No legacy player effect. | The stripped Linux i386 object matches upstream. |
| File handles and removal | [`com_files.c`](../../src/PC/universal/com_files.c), `FS_HandleForFile`, `FS_Shutdown`, `FS_Read`, `FS_DisplayPath`, `FS_OpenFileOverwrite`, `FS_WriteFile`, `FS_FOpenTextFileWrite`, `FS_FOpenFileAppend` | Spells the handle bounds as 61 and 74 and calls `remove` directly on write failure. | Expresses the same bounds arithmetically and calls `FS_Remove`, which forwards to libc `remove` on the legacy path. No legacy player effect. | The stripped Linux i386 object matches upstream. |
| Sound global expression | [`snd_driver.c`](../../src/PC/win32/snd_driver.c), `SND_StartAlias2DSample`, `SND_StartAlias3DSample`, `SND_StartAliasStreamOnChannel`, `SND_Update3DChannel`, `SND_InitDriver`, `SND_SetStreamChannelFromSaveInfo` | Dereferences the imported sound-global pointer directly. | Parenthesizes the same dereference in the macro that selects the native or legacy pointer. No legacy player effect. | The stripped Linux i386 object matches upstream. |
| Ban-list symbol spelling | [`bss.c`](../../src/blobs/bss.c), file-scope `g_banIPs_dvar` alias | Emits the ELF symbol spelling in the assembly alias even on Apple i386. | Emits leading underscores for Mach-O and 32-bit native PE, matching those ABIs. This affects symbol resolution at link time. | Both Apple i386 source projections show the prefix change. The Linux i386 object matches upstream. Mach-O i386 linking was outside the comparison. |

## Checking the record

Each entry names a source file and any Linux i386 objects with a code or data
difference. The `reviewed` commit pins the exact source projections and provides
a same-toolchain control build. A changed or missing recorded difference fails.
An additional edit in a listed file or object must also match the reviewed
result. Update the record and recheck the projections and object controls when
advancing the reviewed commit for a newly approved difference. See
[parity verification](parity.md#ci-and-the-x86-reference) for the commands and
the known upstream build failures.

```json
{
  "upstream": "410342a82da1b2e8286c290274aeb0f1731a6859",
  "reviewed": "d6674f1a8532a40bc68bb42fa3c9a52d43146f70",
  "entries": [
    {
      "id": "Query allocation",
      "source": "src/Mac/DirectX_9/CDirect3DDevice.c",
      "objects": ["CMakeFiles/cod2_objs.dir/src/Mac/DirectX_9/CDirect3DDevice.c.o"]
    },
    {
      "id": "Clipboard byte",
      "source": "src/PC/client_mp/cl_keys_mp.c",
      "objects": ["CMakeFiles/cod2_objs.dir/src/PC/client_mp/cl_keys_mp.c.o"]
    },
    {
      "id": "Screenshot queue",
      "source": "src/PC/gfx_d3d/r_screenshot.c",
      "objects": ["CMakeFiles/cod2_objs.dir/src/PC/gfx_d3d/r_screenshot.c.o"]
    },
    {
      "id": "Favorite selection",
      "source": "src/PC/ui_mp/ui_main_mp.c",
      "objects": ["CMakeFiles/cod2_objs.dir/src/PC/ui_mp/ui_main_mp.c.o"]
    },
    {
      "id": "Fatal exit status",
      "source": "src/Mac/Main/mac_main.c",
      "objects": [
        "CMakeFiles/cod2_linux.dir/src/Mac/Main/mac_main.c.o",
        "CMakeFiles/cod2_lnxded.dir/src/Mac/Main/mac_main.c.o"
      ]
    },
    {"id": "Specularity prototype", "source": "src/PC/gfx_d3d/r_image_load_obj.c"},
    {"id": "Directory name alias", "source": "src/Mac/Main/mac_common.c"},
    {"id": "DPVS cell declaration", "source": "src/PC/gfx_d3d/r_dpvs.c"},
    {"id": "Vertex color block", "source": "src/PC/gfx_d3d/rb_shade.c"},
    {"id": "Depth target predicate", "source": "src/PC/gfx_d3d/rb_state.c"},
    {"id": "File handles and removal", "source": "src/PC/universal/com_files.c"},
    {"id": "Sound global expression", "source": "src/PC/win32/snd_driver.c"},
    {"id": "Ban-list symbol spelling", "source": "src/blobs/bss.c"}
  ]
}
```
