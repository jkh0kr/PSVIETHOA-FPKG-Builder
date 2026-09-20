# Khảo sát ổ game `F:\` và `G:\` — chẩn đoán lỗi đóng gói và phương án xử lý

Ngày 20/09/2026 · công cụ: `scripts/research/survey-games.py` (chỉ đọc) + `fpkg-cli inspect` + `fpkg-cli pkg-info/pkg-extract`.
**Không có thao tác ghi/xoá/đổi tên nào trên `F:\` hay `G:\`.** Mọi đầu ra nằm ở thư mục tạm và `docs/research/`.

Dữ liệu thô: `game-survey-F.json/.md`, `game-survey-G.json/.md`.

---

## 1. Tổng quan hai ổ

| | `F:\` | `G:\` (Extreme SSD) |
|---|---|---|
| Hệ tệp | exFAT | exFAT |
| Dung lượng / trống | 3 815 GB / 356 GB | 3 726 GB / 866 GB |
| **Cluster (block)** | **128 KB** | **1 MB** |
| Game có `sce_sys/param.json` | 31 | 23 |
| Tổng tệp | 494 671 | 318 885 |
| Dữ liệu game (logical) | 3,03 TiB | 2,05 TiB |
| **Tệp rác hệ điều hành** | **222 874** | **17 123** |

**Tổng: 54 game · 813 556 tệp · 5,08 TiB · 239 997 tệp rác (29,5 % tổng số tệp).**

Cả 54 game đều có đủ `sce_sys/` + `eboot.bin` + `icon0.png`, và **keystone đều đúng 96 byte** — không game nào thiếu điều kiện bắt buộc của toolchain SDK.

---

## 2. Lỗi được report: `NAPS metadata missing`

```
[2/2] Building plaintext/no-auth patch against: …Blasphemous2-…-P30Day.ir.pkg
[Debug]  Encrypting and writing files.
[Error]  Unexpected logical error. (NAPS metadata missing)
```

### Nguyên nhân

Trong `libs/sony-sdk/toolchain/libScePubTools.dll` có chuỗi:

```
prev_suppl/common/etc/naps_meta_18.dat
NAPS metadata missing
```

Khi chạy `img_create --ref_pkg_path <gói gốc>`, Publishing Tools đọc **vùng supplement (SI)** của gói gốc để lấy
`common/etc/naps_meta_*.dat`. Gói gốc không có dữ liệu đó → báo `NAPS metadata missing`.

Điểm chết người: lỗi chỉ xuất hiện **sau khi đã nén xong toàn bộ gói** (log của người report: 3 phút 56 giây cho một
game 3 GB; với game 100 GB là vài tiếng). Trước bản sửa này công cụ không hề kiểm tra trước.

### Đã kiểm chứng trên gói thật

| Gói | Vùng supplement | `naps_meta_*.dat` |
|---|---|---|
| Ghost of Yōtei `UP9000-…` (110 GB) | 179 MB | có |
| GTA V `EP1004-…` (96 GB, công cụ này tạo) | 108 MB | có |
| Stellar Blade `EP9000-…` (37 GB) | 66 MB | có |
| FFVII Rebirth `EP0082-…` (143 GB) | 259 MB | có |
| Stellar Blade DLC (712 KB) | 8 KB | có |
| **DUPLEX DLC Unlocker** (đóng gói lại, 1 MB) | 10 KB | **vẫn có** |

Đáng chú ý: ngay cả gói `_DUPLEX_` do bên thứ ba phát hành cũng có đủ `naps_meta`, tức chúng được tạo bằng SDK thật.

### Giới hạn của bản sửa — nói rõ để không hiểu nhầm

Tôi **không có** tệp `Blasphemous2-…P30Day.ir.pkg` để tái hiện, và trên hai ổ này **không có gói nào thiếu NAPS**.
Vì vậy bản sửa là **một lớp chặn đúng hướng, chưa phải bản vá đã chứng minh trên đúng tệp gây lỗi**:

- Chặn chắc chắn trường hợp gói **không có vùng supplement** (khi đó SDK chắc chắn hỏng) — chi phí bằng 0, đọc sẵn từ header.
- Trường hợp **có supplement nhưng thiếu `naps_meta` bên trong** thì lớp chặn này chưa bắt được. Kiểm tra sâu cần
  giải nén SI (đo thực tế: 2,2 giây + ghi 103 MB tạm cho gói 96 GB) nên tôi **không** đưa vào mọi lượt build.
  Khi cần chẩn đoán một gói cụ thể, chạy:
  ```
  fpkg-cli pkg-extract "<gói>.pkg" --output <thư mục> --cnt --include "sce_sys/param.json"
  ```
  rồi xem thư mục `si/` có `common/etc/naps_meta_18.dat` không.

**Cách xử lý dứt điểm cho người dùng:** gói tham chiếu để tạo bản vá phải là gói do chính công cụ này tạo ra.
Gói tải lại từ trang chia sẻ (bản `P30Day`, `fake`, `.ir`) đã bị đóng gói lại, không dùng làm tham chiếu được.
Muốn ra gói đầy đủ thì để trống ô gói gốc.

---

## 3. Vì sao "đóng gói xong chơi lỗi" — phân nhóm rủi ro

### Nhóm 1 — Game dùng `sce::Ampr` (14 game)

`eboot.bin` gọi `libSceAmpr`. **Đây KHÔNG phải dấu vết bị vá** — tôi đã kiểm chứng: với mọi game có cả
`eboot.bin` và bản sao lưu `eboot.bin.esbak`, **hai tệp có trạng thái AMPR y hệt nhau**:

| Game | `eboot.bin` | `eboot.bin.esbak` |
|---|---|---|
| Ghost of Tsushima DC | có AMPR | có AMPR |
| PPSA08772-app | có AMPR | có AMPR |
| FATAL FRAME II | có AMPR | có AMPR |
| Monster Hunter Wilds | sạch | sạch |
| Resident Evil 7 | sạch | sạch |
| Alan Wake Remastered | sạch | sạch |

`libSceAmpr` là thư viện PS5 thật (AMM + APR — quản lý bộ nhớ và nạp asset tốc độ cao). Game nào dùng thì eboot gốc
đã gọi sẵn. Gói của các game này **có thể treo ở màn hình splash khi cài trên PS5** — hạn chế ở phía hệ thống,
không builder nào sửa được. Cách chạy được: để dạng thư mục qua ShadowMount/itemzflow.

DREDGE · Ghost of Tsushima DC · Horizon Zero Dawn Remastered · No Man's Sky · Armored Core 6 · PPSA08772-app ·
PPSA17905-app · Bendy and The Ink Machine · Bendy and The Dark Revival · FATAL FRAME II · MADiSON ·
The Last of Us Part I · The Last of Us Part 2 Remastered · Hitman WoA

### Nhóm 2 — Có bộ giả lập DLC (6 game)

Dying Light 2 · AC Valhalla · Nioh 3 · FATAL FRAME II · Far Cry 6 · PPSA04452-app

Có `dlc_emu.ini` + `fakelib/libSceAppContent.sprx`, `libSceNpEntitlementAccess.sprx` (vài game thêm
`libSceGameUpdate.sprx`). Các thư viện giả này chỉ hoạt động ở chế độ thư mục. Đóng vào gói ứng dụng thì DLC
**không** vào được — DLC phải tách thành gói `addcont` riêng (dùng tab DLC của công cụ).

### Nhóm 3 — Game Capcom RE Engine (RE4, RE7, RE Requiem, PRAGMATA, Monster Hunter Wilds)

Đây là nhóm anh nêu đích danh. Những điểm tìm được:

- **Chuỗi `re_chunk_*.pak.patch_NNN.pak` phải liền số.** RE Engine nạp lần lượt và dừng ở số đầu tiên bị thiếu.
  PRAGMATA có `build/re_chunk_000.pak.patch_003.pak` **chưa được chép ra thư mục gốc** (gốc mới có tới `patch_002`),
  nên bản dịch mới nhất không được nạp.
- **RE4 thiếu hẳn `dlc3`**: các thư mục đi `dlc1, dlc2, [thiếu dlc3], dlc4 … dlc28`.
- `natives/PS5/runtime.args` của các game này chứa cấu hình engine (RE Requiem có `/Wwise/IsEnableWwise: False`,
  `/ScriptDefinitions: APP_DEBUG…`, `/LocalSettingsEnabled: True` + `/LocalSettingsPath: .\config.ini`).
  `LocalSettingsPath` trỏ tới đường dẫn ghi được — khi cài từ gói thì `/app0/` **chỉ đọc**.
  *Chưa kiểm chứng được tệp này có trong bản bán lẻ hay không, nên đây là điểm nghi vấn cần test trên PS5, không phải kết luận.*
- Thư mục `natives/` của RE Requiem và PRAGMATA **chỉ chứa `runtime.args` + tệp rác** (`.DS_Store`, `._PS5`).

### Nhóm 4 — Tên tệp/thư mục đặc biệt (8 game)

| Game | Vấn đề |
|---|---|
| Ghost of Tsushima DC | **10 894 tệp có `#`** trong tên (`…_poly_ja#tandem#11.xpps`) |
| ELDEN RING | `[rt]` trong tên + **924 tệp 0 byte** |
| Armored Core 6 | `[rt]` trong tên |
| AC Shadows | dấu phẩy (`40,000Strong`) |
| KARMA The Dark World | dấu nháy đơn (`fred' secret room`) |
| SAROS, Returnal | dấu phẩy |
| Dying Light 2 | `#align_0.txt` |

**Đã kiểm chứng:** tạo GP5 cho Ghost of Tsushima (40 820 tệp), KARMA và Frostpunk 2 đều thành công, XML hợp lệ.
Nhóm này **không** gây lỗi.

### Nhóm 5 — Tên ngoài ASCII

- `G:\PPSA01467-app Marvels Spider Man Remastered` + `G:\PPSA03396 The Last of Us Part I` — **có ký tự ẩn U+F028
  (vùng Private Use) ở cuối tên thư mục**. Không nhìn thấy trên Explorer. Đây là ký tự do lớp dịch tên của
  macOS/NAS sinh ra khi chép sang exFAT. Ký tự này làm hỏng công cụ nào không xử lý Unicode (chính script khảo sát
  của dự án cũng crash vì nó trước khi tôi ép `PYTHONUTF8=1`).
- `F:\PPSA01461-app0 Marvel’s Spider-Man Miles Morales` — dấu nháy cong U+2019.
- `PPSA26344 Ghost of Yōtei (01.512.000)-pkg` — chữ `ō`.

Công cụ đã có cơ chế bí danh ASCII (`SonySdkPathAliases`) cho các trường hợp này, và cả 54 game đều qua
`fpkg-cli inspect`.

---

## 4. Rác hệ điều hành — con số thật

Hai ổ đều là exFAT và đã từng được chép qua macOS/NAS Synology (`@eaDir`, `@tmp` ở gốc ổ), nên mỗi tệp thật đi kèm
một tệp AppleDouble `._<tên>` 4 KB.

**Vì exFAT không lưu tệp nhỏ trong bảng thư mục, mỗi tệp rác chiếm trọn một cluster:**

| Ổ | Tệp rác | Dữ liệu thật | **Chiếm đĩa** |
|---|---:|---:|---:|
| F: (cluster 128 KB) | 222 874 | 0,85 GiB | **27,2 GiB** |
| G: (cluster 1 MB) | 17 123 | 0,07 GiB | **16,7 GiB** |
| | | | **≈ 43,9 GiB** |

Nặng nhất: ELDEN RING 151 706 · Nioh 3 35 509 · Armored Core 6 23 728 · FATAL FRAME II 11 359 ·
Dragon Ball Z Kakarot 8 460 · Frostpunk 2 1 291.

Ngoài ra có lối tắt quảng cáo tên tiếng Trung `更多资源请访问 2468c.com.url` (Cyberpunk 2077 trên F:, Frostpunk 2 và
PRAGMATA trên G:) — game PS5 không bao giờ chứa tệp `.url`.

> **Lưu ý quan trọng:** công cụ **đã tự loại toàn bộ nhóm rác này** khỏi gói trên **cả hai** đường chạy
> (thư mục → `SkipJunk: true`; ảnh gắn Dokan → mount đã ẩn sẵn). Tôi đã dựng GP5 thử cho Bendy (73 tệp),
> Ghost of Tsushima (40 820 tệp), KARMA (128 tệp), Frostpunk 2 (823 tệp) — **không gói nào lọt tệp rác**.
> Dọn ổ **không bắt buộc** để gói chạy đúng; lợi ích là lấy lại ~44 GiB và duyệt thư mục nhanh hơn.

---

## 5. Việc nên làm trên ổ game

Tất cả đều **tuỳ chọn**, xếp theo mức lợi ích. Không mục nào là điều kiện để gói chạy đúng.

**a) Dọn rác OS — lấy lại ~44 GiB.** Xem trước trước khi xoá:

```powershell
fpkg-cli clean-junk "G:\PPSA02530 PRAGMATA" --dry-run     # chỉ liệt kê
fpkg-cli clean-junk "G:\PPSA02530 PRAGMATA"               # xoá thật
```

**b) Chép bản dịch PRAGMATA còn kẹt:** `build\re_chunk_000.pak.patch_003.pak` → thư mục gốc của game.
Nếu không, bản dịch mới nhất không vào game.

**c) Kiểm tra `dlc3` của RE4** — đang thiếu trong dãy `dlc1…dlc28`.

**d) Đổi tên 2 thư mục có ký tự ẩn U+F028** (`Marvels Spider Man Remastered`, `The Last of Us Part I` trên G:) —
gõ lại tên bằng tay để bỏ ký tự cuối. Giúp mọi công cụ khác khỏi vấp.

**e) Cân nhắc cluster 1 MB của `G:\`.** Đây là lựa chọn format bất lợi cho ổ chứa nhiều tệp nhỏ (riêng Returnal
286 230 tệp). Ước tính hao hụt ~156 GiB — *đây là ước tính theo mô hình nửa cluster/tệp, chưa đo trực tiếp*.
Format lại với cluster 128 KB cần chép toàn bộ dữ liệu đi nơi khác, nên chỉ nên làm khi tiện.

**Không nên xoá:** `eboot.bin.esbak` (bản sao lưu eboot gốc, công cụ đã tự loại khỏi gói),
`fakelib/` (công cụ tự lọc đúng module cần bỏ), `ampr_emu.index` (đã bị loại khỏi gói).

---

## 6. Thay đổi mã trong lượt này

| Tệp | Thay đổi |
|---|---|
| `Core/Services/SonySdkPatchReference.cs` | `Inspect()` chặn ngay gói tham chiếu không có vùng supplement, kèm thông báo chỉ rõ cách xử lý — thay vì để SDK nén xong mới báo `NAPS metadata missing` |
| `Core/Services/BuildEngine.cs` | Thêm `WarnAmprGame()`, gọi tại điểm hội tụ trước mọi nhánh build (SDK thường / SDK + Dokan / bản vá / engine tích hợp) → cảnh báo **trước khi nén** khi game dùng `sce::Ampr` |
| `Core/Localization/vi.json`, `en.json` | Khoá `Patch.ReferenceNoNaps`, `Plan.AmprGame` |

`AmprInspector` trước đây chỉ được dùng ở `inspect` và lúc mở nguồn (mức Info, lẫn giữa các dòng khác);
luồng build không hề cảnh báo. Chính chú thích trong `AmprInspector.cs` đã ghi mục đích *"cảnh báo game dùng AMPR
có thể treo ở màn hình splash khi cài từ gói"* nhưng chưa được nối vào build.

**Kiểm thử:** `dotnet build` sạch lỗi. `dotnet test`: **332 pass / 1 fail** — đúng bằng baseline trên cây sạch
(`SonySdkTests.Build_WorksWithNonAsciiSourceAndOutputPaths` hỏng sẵn từ trước, tôi đã xác minh bằng `git stash`).
**Không hồi quy.**

### Một thay đổi đã thử rồi bỏ

Tôi có sửa `libs/sony-sdk/scripts/create-gp5-from-folder.py` để nó cũng lọc rác (script gốc không lọc). Sau đó **hoàn
tác**, vì:

- Ứng dụng dùng bản port C# (`SonySdkProject.Create`), **không bao giờ gọi script Python** — nên sửa script không
  giúp gì cho người dùng app.
- Script là bản tham chiếu được test `Gp5_IsByteIdenticalToTheToolkitScript` ghim **byte-for-byte** với bản port C#.
  Sửa một phía làm hỏng test đó (đã xảy ra) và làm hai bản lệch nhau.

Người chạy thẳng toolkit PowerShell (như trong báo cáo lỗi: `…\sdk-fpkg279-fix9-self\build-from-folder.ps1`) vẫn sẽ
đóng cả tệp rác vào gói. Nếu anh muốn xử lý nhóm người dùng đó thì phải sửa **đồng thời** script Python và bản port C#
để test byte-identical vẫn xanh — việc này cần quyết định của anh vì nó đụng vào quy ước "trung thành toolkit".

---

## 7. Còn phải test trên PS5 (tôi không kết luận thay)

1. Gói của 14 game nhóm AMPR — có treo splash thật không.
2. `natives/PS5/runtime.args` với `LocalSettingsEnabled: True` khi `/app0/` chỉ đọc — nhóm Capcom RE.
3. DLC của 6 game nhóm `dlc_emu` sau khi tách thành gói addcont riêng.
4. Gói tạo từ 2 thư mục có ký tự ẩn U+F028.
