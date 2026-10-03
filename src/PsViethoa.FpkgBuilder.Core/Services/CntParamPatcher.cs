using System.Buffers.Binary;
using System.Text;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

public sealed record CntParamPatchReport(long SlotSize, int PaddedBytes, int ResealedBlocks, string OutputPath);

/// <summary>
/// Đường "vá nhanh" cho gói PLAINTEXT_NOAUTH: thay param.json NGUYÊN VĂN trong khe CNT của nó (đệm khoảng trắng cho khớp
/// kích thước — khe cố định nên không xê dịch gì trong gói), rồi niêm phong lại đúng chuỗi của SonySdkConverter: bản ghi
/// digest của mục, bốn dấu niêm thân CNT (rollup / SC / bảng digest / body), hai digest mô tả, tự-hash header, lớp bọc
/// RSA-3072 và bảng CRC32C PlayGo trong kho SI. Không đổi FIH hay ảnh PFS ngoài nên chỉ vài khối 64 KiB là được viết lại.
/// Bản sao giữ nguyên tệp gốc; cả gói được sao chép sang tệp đích trước khi vá. Thử nghiệm: chưa kiểm trên máy thật.
/// </summary>
public static class CntParamPatcher
{
    private const int BlockSize = 0x10000;
    private const int EntryMetaSize = 0x20;
    private const uint EntryFlagEncrypted = 0x80000000;
    private const uint DigestTableEntryId = 0x0001;
    private static readonly byte[] FihMagic = [0x7F, (byte)'F', (byte)'I', (byte)'H'];

    /// <summary>Sao chép gói nguồn sang <paramref name="outputPath"/> rồi vá param.json ở bản sao. Trả về báo cáo.</summary>
    public static CntParamPatchReport Patch(string sourcePath, string outputPath, byte[] newParamJson, Action<double, string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SonySdkConverter.IsAlreadyPlaintext(sourcePath))
        {
            throw new InvalidDataException(Loc.T("FastParam.NotPlaintext"));
        }

        CopyFile(sourcePath, outputPath, progress, cancellationToken);
        progress?.Invoke(65, "re-sealing CNT");
        return PatchInPlace(outputPath, newParamJson, progress, cancellationToken);
    }

    /// <summary>Vá tại chỗ (tệp đích đã là bản sao). Tiện cho kiểm thử.</summary>
    public static CntParamPatchReport PatchInPlace(string path, byte[] newParamJson, Action<double, string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.RandomAccess);
        var fileSize = stream.Length;
        var fih = ReadExact(stream, 0, 0x1000);
        if (!fih.AsSpan(0, 4).SequenceEqual(FihMagic))
        {
            throw new InvalidDataException("only finalized FIH packages are supported");
        }

        var cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x58));
        if (cntOffset <= 0 || cntOffset + BlockSize > fileSize)
        {
            throw new InvalidDataException("FIH does not point to a CNT header");
        }

        var cnt = ReadExact(stream, cntOffset, BlockSize);
        var entries = ParseEntries(cnt);

        // Tìm param.json theo NỘI DUNG (mục JSON duy nhất có "contentId") — không dùng thư viện đọc gói vì nó giữ mở tệp,
        // mà ở đây tệp đang mở ReadWrite (FileShare.None).
        var paramIndex = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if ((e.Flags & EntryFlagEncrypted) != 0 || e.Size < 16 || e.Size > 1 << 20)
            {
                continue;
            }

            var head = ReadExact(stream, cntOffset + e.Offset, (int)Math.Min(e.Size, 4096));
            var text = Encoding.UTF8.GetString(head).TrimStart();
            if (text.StartsWith('{') && text.Contains("\"contentId\"", StringComparison.Ordinal))
            {
                if (paramIndex >= 0)
                {
                    throw new InvalidDataException("several CNT entries look like param.json");
                }

                paramIndex = i;
            }
        }

        if (paramIndex < 0)
        {
            throw new InvalidDataException("CNT has no param.json entry");
        }

        var index = paramIndex;
        var slot = entries[index];
        if (newParamJson.Length > slot.Size)
        {
            throw new InvalidDataException(Loc.F("FastParam.TooLarge", newParamJson.Length, slot.Size));
        }

        var padded = new byte[slot.Size];
        newParamJson.CopyTo(padded, 0);
        Array.Fill(padded, (byte)' ', newParamJson.Length, padded.Length - newParamJson.Length);

        var touched = new SortedSet<long>();
        void MarkTouched(long offset, long size)
        {
            if (size <= 0)
            {
                return;
            }

            for (var block = offset / BlockSize; block <= (offset + size - 1) / BlockSize; block++)
            {
                touched.Add(block);
            }
        }

        void WriteCnt(long relativeOffset, ReadOnlySpan<byte> data)
        {
            if (relativeOffset < 0 || relativeOffset + data.Length > fileSize - cntOffset)
            {
                throw new InvalidDataException("CNT update is outside the package");
            }

            stream.Position = cntOffset + relativeOffset;
            stream.Write(data);
            MarkTouched(cntOffset + relativeOffset, data.Length);
            var first = Math.Max(relativeOffset, 0);
            var last = Math.Min(relativeOffset + data.Length, BlockSize);
            if (first < last)
            {
                data.Slice((int)(first - relativeOffset), (int)(last - first)).CopyTo(cnt.AsSpan((int)first));
            }
        }

        // (1) Dữ liệu mới của param.json — nguyên văn, đệm khoảng trắng cho khớp khe.
        WriteCnt(slot.Offset, padded);

        var digestEntry = entries.FindIndex(e => e.Id == DigestTableEntryId);
        if (digestEntry < 0 || entries[digestEntry].Size < entries.Count * 32L)
        {
            throw new InvalidDataException("CNT per-entry digest table is missing or truncated");
        }

        // (2) Bản ghi digest của chính mục param.json.
        WriteCnt(entries[digestEntry].Offset + index * 32L, HashRange(stream, cntOffset + slot.Offset, slot.Size));

        // (3) Bốn dấu niêm thân CNT — chuỗi giống SonySdkConverter (rollup / SC / bảng digest / body).
        var rollupOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x20));
        var rollupSize = (long)BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x1C));
        RequireRange(fileSize - cntOffset, rollupOffset, rollupSize, "CNT rollup");
        WriteCnt(0x100, HashRange(stream, cntOffset + rollupOffset, rollupSize));

        var scCount = BinaryPrimitives.ReadUInt16BigEndian(cnt.AsSpan(0x14));
        var scEntries = entries.Where(e => e.Id is 0x0010 or 0x0020 or 0x0080 or 0x0100).ToList();
        if (scEntries.Count != 4)
        {
            throw new InvalidDataException("CNT does not contain the expected SC entries");
        }

        var shortRanges = scEntries.Select(e => (cntOffset + e.Offset, (long)e.Size)).ToList();
        shortRanges[^1] = (cntOffset + scEntries[^1].Offset, Math.Min(scEntries[^1].Size, scCount * (long)EntryMetaSize));
        WriteCnt(0x120, HashRanges(stream, shortRanges));
        WriteCnt(0x140, HashRange(stream, cntOffset + entries[digestEntry].Offset, entries[digestEntry].Size));

        var bodyOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x20));
        var bodySize = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(0x28));
        RequireRange(fileSize - cntOffset, bodyOffset, bodySize, "CNT body");
        WriteCnt(0x160, HashRange(stream, cntOffset + bodyOffset, bodySize));

        // (4) Hai digest mô tả (image-key / mandatory) — vùng không đổi nhưng tính lại cho khớp chuỗi gốc.
        var imageKeyOffset = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x510));
        var imageKeySize = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x514));
        var mandatoryOffset = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x518));
        var mandatorySize = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x51C));
        RequireRange(fileSize - cntOffset, imageKeyOffset, imageKeySize, "CNT image-key descriptor");
        RequireRange(fileSize - cntOffset, mandatoryOffset, mandatorySize, "CNT mandatory descriptor");
        var descriptorDigests = new byte[64];
        HashRange(stream, cntOffset + imageKeyOffset, imageKeySize).CopyTo(descriptorDigests, 0);
        HashRange(stream, cntOffset + mandatoryOffset, mandatorySize).CopyTo(descriptorDigests, 32);
        WriteCnt(0x520, descriptorDigests);

        // (5) Tự-hash header CNT + lớp bọc RSA-3072.
        cnt.AsSpan(0xFE0, 32).Clear();
        Sha3(cnt.AsSpan(0, 0xFE0)).CopyTo(cnt.AsSpan(0xFE0));
        WriteAt(stream, cntOffset, cnt);
        MarkTouched(cntOffset, cnt.Length);
        var wrap = ProsperoPublisherRsa.BuildCntHeaderWrap(cnt);
        WriteAt(stream, cntOffset + 0x1000, wrap);
        MarkTouched(cntOffset + 0x1000, wrap.Length);
        stream.Flush();
        progress?.Invoke(85, "rebuilding SI PlayGo CRCs");

        // (6) CRC32C PlayGo trong kho SI cho các khối vừa chạm — tính điểm bắt đầu SI đúng như SonySdkConverter.
        var table = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x18));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x10));
        var cntEnd = Math.Max(0x5A0L, Math.Max(table + (long)count * EntryMetaSize, bodyOffset + bodySize));
        foreach (var e in entries)
        {
            cntEnd = Math.Max(cntEnd, (long)e.Offset + e.Size);
        }

        cntEnd = (cntEnd + 15) & ~15L;
        var supplementOffset = cntOffset + cntEnd;
        if (supplementOffset < fileSize)
        {
            RepairPlayGoCrc(stream, fileSize, supplementOffset, touched);
        }

        stream.Flush();
        progress?.Invoke(100, "done");
        return new CntParamPatchReport(slot.Size, padded.Length - newParamJson.Length, touched.Count, path);
    }

    /// <summary>
    /// Sao chép gói nguồn sang tệp đích. Ổ SSD (không có seek penalty — hỏi trực tiếp driver qua IOCTL trên Windows) chép
    /// SONG SONG 4 luồng theo đoạn 1 MB-can; ổ HDD / USB / mạng chép tuần tự như cũ (song song trên HDD còn chậm hơn).
    /// </summary>
    private static void CopyFile(string source, string target, Action<double, string>? progress, CancellationToken cancellationToken)
    {
        long total;
        using (var probe = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        {
            total = probe.Length;
        }

        var degree = ChooseCopyDegree(source);
        if (degree > 1 && total >= (64L << 20))
        {
            PipelineCopy(source, target, total, degree, progress, cancellationToken);
            return;
        }

        const long chunk = 1 << 24;
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        long done = 0;
        var buffer = new byte[chunk];
        while (done < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.ReadAtLeast(buffer, (int)Math.Min(chunk, total - done), throwOnEndOfStream: false);
            if (read <= 0)
            {
                throw new EndOfStreamException("truncated package while copying");
            }

            output.Write(buffer, 0, read);
            done += read;
            progress?.Invoke(60.0 * done / Math.Max(1, total), "copying");
        }
    }

    /// <summary>
    /// Sao chép đường ống: ĐỌC song song (đầu đĩa SSD chịu QD cao) nhưng GHI một luồng duy nhất THEO ĐÚNG THỨ TỰ chunk.
    /// Ghi tuần tự giữ Valid-Data-Length đi lên liên tục — không phát sinh CcZeroData lấp khoảng trống gigabyte giữa bốn
    /// vùng rải rác như bản ghi song song cũ (thứ từng làm tiến độ "đứng hình" lúc bộ nhớ đệm bẩn đầy), và flush đĩa mỗi
    /// 1 GiB giữ nợ bộ nhớ đệm luôn thấp nên thanh tiến độ chạy mịn từ đầu tới cuối.
    /// </summary>
    private static void PipelineCopy(string source, string target, long total, int degree, Action<double, string>? progress, CancellationToken cancellationToken)
    {
        const int chunkSize = 1 << 23; // 8 MiB
        var chunkCount = (total + chunkSize - 1) / chunkSize;
        var ringSize = Math.Max(4, degree * 2);
        var ring = new byte[ringSize][];
        for (var i = 0; i < ringSize; i++)
        {
            ring[i] = new byte[chunkSize];
        }

        // 0 = rảnh (đọc được), 1 = đang đọc, 2 = sẵn sàng ghi, 3 = đang ghi.
        var state = new int[ringSize];
        var gate = new object();
        long nextRead = 0;
        long nextWrite = 0;
        Exception? failure = null;

        var readers = new Thread[degree];
        for (var index = 0; index < degree; index++)
        {
            readers[index] = new Thread(() =>
            {
                try
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22, FileOptions.SequentialScan);
                    while (true)
                    {
                        var slot = 0;
                        lock (gate)
                        {
                            while (failure == null && (nextRead >= chunkCount || !TryClaimFreeSlot(state, out slot)))
                            {
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    return;
                                }

                                Monitor.Wait(gate, 200);
                            }

                            if (failure != null || nextRead >= chunkCount)
                            {
                                return;
                            }

                            nextRead++;
                            state[slot] = 1;
                        }

                        var chunk = nextRead - 1;
                        var offset = chunk * chunkSize;
                        var size = (int)Math.Min(chunkSize, total - offset);
                        input.Position = offset;
                        input.ReadExactly(ring[slot], 0, size);

                        lock (gate)
                        {
                            state[slot] = 2;
                            Monitor.PulseAll(gate);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        failure ??= ex;
                        Monitor.PulseAll(gate);
                    }
                }
            })
            {
                IsBackground = true,
            };
            readers[index].Start();
        }

        long done = 0;
        try
        {
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 23, FileOptions.SequentialScan);
            var sinceFlush = 0L;
            while (nextWrite < chunkCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = (int)(nextWrite % ringSize);
                lock (gate)
                {
                    while (state[slot] != 2)
                    {
                        if (failure != null)
                        {
                            throw failure;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        Monitor.Wait(gate, 200);
                    }

                    state[slot] = 3;
                }

                var offset = nextWrite * chunkSize;
                var size = (int)Math.Min(chunkSize, total - offset);
                output.Write(ring[slot], 0, size);
                nextWrite++;
                done += size;
                sinceFlush += size;
                if (sinceFlush >= (1L << 30))
                {
                    // Giữ nợ bộ nhớ đệm ghi thấp: flush xuống đĩa từng 1 GiB thay vì để dồn 20+ GiB rồi đứng hình một cục.
                    output.Flush(flushToDisk: true);
                    sinceFlush = 0;
                }

                progress?.Invoke(60.0 * done / total, "copying");

                lock (gate)
                {
                    state[slot] = 0;
                    Monitor.PulseAll(gate);
                }
            }

            output.Flush(flushToDisk: true);
        }
        finally
        {
            lock (gate)
            {
                failure ??= new OperationCanceledException(cancellationToken);
                Monitor.PulseAll(gate);
            }

            foreach (var reader in readers)
            {
                reader?.Join(5000);
            }
        }
    }

    private static bool TryClaimFreeSlot(int[] state, out int slot)
    {
        for (var i = 0; i < state.Length; i++)
        {
            if (state[i] == 0)
            {
                slot = i;
                return true;
            }
        }

        slot = -1;
        return false;
    }

    /// <summary>SSD (không seek penalty) → 4 luồng; USB/đĩa quang → tuần tự; mạng → 2; không dò được → 4.</summary>
    private static int ChooseCopyDegree(string source)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(source));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                return 2; // UNC/đường dẫn mạng
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network)
            {
                return 2;
            }

            if (drive.DriveType != DriveType.Fixed)
            {
                return 1; // USB / thẻ nhớ: tuần tự an toàn
            }

            return OperatingSystem.IsWindows() && VolumeIncursSeekPenalty(root[0]) ? 1 : 4;
        }
        catch (Exception)
        {
            return 4;
        }
    }

    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;

    /// <summary>Hỏi driver ổ đĩa (Windows): ổ này có bị phạt seek (HDD) không? Trả về true cho HDD, false cho SSD.</summary>
    private static bool VolumeIncursSeekPenalty(char driveLetter)
    {
        var invalid = new IntPtr(-1);
        var volume = CreateFile($"\\\\.\\{driveLetter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume == invalid)
        {
            throw new InvalidOperationException("cannot open volume");
        }

        try
        {
            var number = new byte[12];
            if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, null, 0, number, (uint)number.Length, out _, IntPtr.Zero))
            {
                throw new InvalidOperationException("cannot query device number");
            }

            var disk = (uint)(number[4] | (number[5] << 8) | (number[6] << 16) | (number[7] << 24));
            var physical = CreateFile($"\\\\.\\PhysicalDrive{disk}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (physical == invalid)
            {
                throw new InvalidOperationException("cannot open physical drive");
            }

            try
            {
                var query = new byte[9];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(query, StorageDeviceSeekPenaltyProperty);
                var result = new byte[12];
                if (!DeviceIoControl(physical, IoctlStorageQueryProperty, query, (uint)query.Length, result, (uint)result.Length, out var returned, IntPtr.Zero) || returned < 9)
                {
                    throw new InvalidOperationException("cannot query seek penalty");
                }

                return result[8] != 0;
            }
            finally
            {
                CloseHandle(physical);
            }
        }
        finally
        {
            CloseHandle(volume);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, byte[]? lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Sửa CRC32C PlayGo trong kho SI CHỈ những gì cần: đọc EOCD (cuối kho) → thư mục trung tâm (vài KB) → mục
    /// playgo-chunk.crc (~số khối × 4 byte), cập nhật CRC các khối vừa chạm rồi ghi lại đúng vị trí mục + 2 trường
    /// CRC-32 của ZIP — không đọc/ghi lại cả kho SI hàng chục MB như trước.
    /// </summary>
    private static void RepairPlayGoCrc(FileStream stream, long fileSize, long supplementOffset, SortedSet<long> touched)
    {
        var blockCount = (supplementOffset + BlockSize - 1) / BlockSize;
        var contentIdBytes = ReadExact(stream, GetCntOffset(stream), 0x70).AsSpan(0x40, 0x30);
        var terminator = contentIdBytes.IndexOf((byte)0);
        var contentId = Encoding.ASCII.GetString(terminator < 0 ? contentIdBytes.ToArray() : contentIdBytes[..terminator].ToArray());
        var crcName = $"config/{contentId}/playgo-chunk.crc";

        var supplementSize = fileSize - supplementOffset;
        var tailLength = (int)Math.Min(supplementSize, 0x10016 + 22);
        if (tailLength < 22)
        {
            return;
        }

        var tail = ReadExact(stream, fileSize - tailLength, tailLength);
        var eocd = LastIndexOf(tail, "PK\x05\x06"u8, 0);
        if (eocd < 0 || eocd + 22 > tail.Length)
        {
            return; // bố cục khác dự kiến: bỏ qua sửa CRC, phần còn lại đã niêm phong đúng
        }

        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (centralSize > supplementSize || centralOffset + (long)centralSize > supplementSize - 22)
        {
            return;
        }

        var central = ReadExact(stream, supplementOffset + centralOffset, (int)centralSize);
        var member = FindCrcMember(stream, supplementOffset, central, centralSize, crcName);
        var current = ReadExact(stream, supplementOffset + member.DataOffset, member.Size);
        if (current.Length != blockCount * 4)
        {
            return; // bố cục khác dự kiến: bỏ qua sửa CRC
        }

        foreach (var blockIndex in touched)
        {
            if (blockIndex >= blockCount)
            {
                continue;
            }

            var blockOffset = blockIndex * BlockSize;
            var size = (int)Math.Min(BlockSize, supplementOffset - blockOffset);
            var block = ReadExact(stream, blockOffset, size);
            BinaryPrimitives.WriteUInt32LittleEndian(current.AsSpan((int)blockIndex * 4), ProsperoCrc32C.Compute(block));
        }

        var crc = SonySdkConverter.ZipCrc32(current);
        WriteAt(stream, supplementOffset + member.DataOffset, current);
        WriteAt(stream, supplementOffset + member.LocalHeader + 14, [unchecked((byte)crc), (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)]);
        WriteAt(stream, supplementOffset + member.CentralRecord + 16, [unchecked((byte)crc), (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)]);
    }

    private readonly record struct SiMember(int LocalHeader, int DataOffset, int Size, int CentralRecord);

    /// <summary>Tìm mục ZIP STORED theo tên trong thư mục trung tâm (đã đọc sẵn) rồi đọc header cục bộ để tính DataOffset.</summary>
    private static SiMember FindCrcMember(FileStream stream, long supplementOffset, byte[] central, uint centralSize, string name)
    {
        SiMember? found = null;
        var cursor = 0;
        while (cursor < centralSize)
        {
            if (cursor + 46 > central.Length || !central.AsSpan(cursor, 4).SequenceEqual("PK\x01\x02"u8))
            {
                throw new InvalidDataException("invalid SI central-directory record");
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 10));
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 20));
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 24));
            var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 28));
            var extraSize = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 30));
            var commentSize = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 32));
            var localHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 42));
            if (cursor + 46 + nameSize > central.Length)
            {
                throw new InvalidDataException("invalid SI central-directory record");
            }

            var entryName = Encoding.UTF8.GetString(central, cursor + 46, nameSize);
            if (entryName == name)
            {
                if (found != null)
                {
                    throw new InvalidDataException($"SI archive must contain exactly one {name}");
                }

                if (method != 0 || (flags & 0x08) != 0 || compressedSize != uncompressedSize)
                {
                    throw new InvalidDataException($"SI member is not a directly patchable STORED entry: {name}");
                }

                var local = ReadExact(stream, supplementOffset + localHeader, 34);
                if (!local.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8))
                {
                    throw new InvalidDataException($"invalid SI local header for {name}");
                }

                var localNameSize = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26));
                var localExtraSize = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
                found = new SiMember(localHeader, localHeader + 30 + localNameSize + localExtraSize, (int)uncompressedSize, cursor);
            }

            cursor += 46 + nameSize + extraSize + commentSize;
        }

        return found ?? throw new InvalidDataException($"SI archive must contain exactly one {name}");
    }

    private static long GetCntOffset(FileStream stream)
    {
        stream.Position = 0x58;
        var buffer = new byte[8];
        stream.ReadExactly(buffer);
        return (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private readonly record struct Entry(uint Id, uint Flags, uint Offset, uint Size);

    private static List<Entry> ParseEntries(byte[] header)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10));
        var table = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18));
        if (count == 0 || count > 0x10000 || table + (long)count * EntryMetaSize > header.Length)
        {
            throw new InvalidDataException("invalid CNT entry table");
        }

        var entries = new List<Entry>((int)count);
        for (var index = 0; index < count; index++)
        {
            var record = (int)table + index * EntryMetaSize;
            entries.Add(new Entry(
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record + 8)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record + 0x10)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(record + 0x14))));
        }

        return entries;
    }

    private static void RequireRange(long fileSize, long offset, long size, string name)
    {
        if (offset < 0 || size < 0 || offset > fileSize || size > fileSize - offset)
        {
            throw new InvalidDataException($"{name} is outside the package");
        }
    }

    private static byte[] ReadExact(FileStream stream, long offset, int size)
    {
        var buffer = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WriteAt(FileStream stream, long offset, ReadOnlySpan<byte> data)
    {
        stream.Position = offset;
        stream.Write(data);
    }

    private static byte[] Sha3(ReadOnlySpan<byte> data)
    {
        var digest = new byte[32];
        ProsperoSha3.HashData(data, digest);
        return digest;
    }

    private static byte[] HashRange(FileStream stream, long offset, long size) =>
        size == 0 ? Sha3(ReadOnlySpan<byte>.Empty) : Crypto.Sha3_256(stream, offset, size);

    private static byte[] HashRanges(FileStream stream, IReadOnlyList<(long Offset, long Size)> ranges)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 20];
        foreach (var (offset, size) in ranges)
        {
            stream.Position = offset;
            var remaining = size;
            while (remaining > 0)
            {
                var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0)
                {
                    throw new InvalidDataException($"truncated package at 0x{offset:X}");
                }

                buffer.Write(chunk, 0, read);
                remaining -= read;
            }
        }

        return Sha3(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern, int minimum)
    {
        var index = data.AsSpan(minimum).LastIndexOf(pattern);
        return index < 0 ? -1 : minimum + index;
    }
}
