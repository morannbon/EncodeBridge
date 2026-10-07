using EncodeBridge.Domain;

namespace EncodeBridge.Transport;

internal sealed class TsServiceInspector
{
    private const int PacketSize = 188;

    public TsServiceInfo Inspect(string filePath, ushort targetServiceId)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var assemblers = new Dictionary<ushort, PsiAssembler>();
        var programs = new Dictionary<ushort, ushort>();
        ushort? pmtPid = null;
        var video = new HashSet<ushort>();
        var audio = new HashSet<ushort>();
        var captions = new HashSet<ushort>();
        var packet = new byte[PacketSize];
        var maxBytes = Math.Min(fs.Length, 64L * 1024 * 1024);
        long readBytes = 0;

        while (readBytes < maxBytes && fs.Read(packet, 0, packet.Length) == packet.Length)
        {
            readBytes += PacketSize;
            if (packet[0] != 0x47) continue;
            var pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
            if (pid != 0x0000 && (!pmtPid.HasValue || pid != pmtPid.Value)) continue;

            var payloadStart = (packet[1] & 0x40) != 0;
            var afc = (packet[3] >> 4) & 0x03;
            if ((afc & 0x01) == 0) continue;
            var pos = 4;
            if ((afc & 0x02) != 0)
            {
                if (pos >= PacketSize) continue;
                pos += 1 + packet[pos];
                if (pos >= PacketSize) continue;
            }

            if (!assemblers.TryGetValue(pid, out var assembler))
                assemblers[pid] = assembler = new PsiAssembler();

            foreach (var section in assembler.Push(packet.AsSpan(pos), payloadStart))
            {
                if (pid == 0x0000) ParsePat(section, programs, targetServiceId, ref pmtPid);
                else if (pmtPid.HasValue && pid == pmtPid.Value) ParsePmt(section, targetServiceId, video, audio, captions);
            }

            if (pmtPid.HasValue && video.Count > 0 && programs.Count > 0)
                break;
        }

        if (!pmtPid.HasValue) throw new InvalidDataException("録画対象のサービス情報をTSから確認できませんでした。");
        if (video.Count == 0) throw new InvalidDataException("録画対象の映像をTSから確認できませんでした。");

        return new TsServiceInfo
        {
            ServiceId = targetServiceId,
            PmtPid = pmtPid.Value,
            VideoPids = video.OrderBy(x => x).ToArray(),
            AudioPids = audio.OrderBy(x => x).ToArray(),
            CaptionPids = captions.OrderBy(x => x).ToArray(),
            ProgramCount = programs.Count
        };
    }

    private static void ParsePat(byte[] section, Dictionary<ushort, ushort> programs, ushort targetSid, ref ushort? pmtPid)
    {
        if (section.Length < 12 || section[0] != 0x00) return;
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(section.Length - 4, 3 + sectionLength - 4);
        for (var pos = 8; pos + 4 <= end; pos += 4)
        {
            var program = (ushort)((section[pos] << 8) | section[pos + 1]);
            var pid = (ushort)(((section[pos + 2] & 0x1F) << 8) | section[pos + 3]);
            if (program == 0) continue;
            programs[program] = pid;
            if (program == targetSid) pmtPid = pid;
        }
    }

    private static void ParsePmt(byte[] section, ushort targetSid, HashSet<ushort> video, HashSet<ushort> audio, HashSet<ushort> captions)
    {
        if (section.Length < 16 || section[0] != 0x02) return;
        var programNumber = (ushort)((section[3] << 8) | section[4]);
        if (programNumber != targetSid) return;
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        var end = Math.Min(section.Length - 4, 3 + sectionLength - 4);
        for (var pos = 12 + programInfoLength; pos + 5 <= end;)
        {
            var streamType = section[pos];
            var esPid = (ushort)(((section[pos + 1] & 0x1F) << 8) | section[pos + 2]);
            var esInfoLength = ((section[pos + 3] & 0x0F) << 8) | section[pos + 4];
            switch (streamType)
            {
                case 0x01: case 0x02: case 0x1B: case 0x24: video.Add(esPid); break;
                case 0x03: case 0x04: case 0x0F: case 0x11: audio.Add(esPid); break;
                case 0x06:
                    if (LooksLikeCaptionDescriptor(section, pos + 5, Math.Min(esInfoLength, end - (pos + 5)))) captions.Add(esPid);
                    break;
            }
            pos += 5 + esInfoLength;
        }
    }

    private static bool LooksLikeCaptionDescriptor(byte[] data, int offset, int length)
    {
        var end = Math.Min(data.Length, offset + Math.Max(0, length));
        for (var pos = offset; pos + 2 <= end;)
        {
            var tag = data[pos];
            var len = data[pos + 1];
            if (tag is 0x52 or 0xFD) return true;
            if (pos + 2 + len > end) break;
            pos += 2 + len;
        }
        return false;
    }

    private sealed class PsiAssembler
    {
        private readonly List<byte> _buffer = new();
        private int? _expected;

        public IEnumerable<byte[]> Push(ReadOnlySpan<byte> payload, bool payloadUnitStart)
        {
            var sections = new List<byte[]>();
            var pos = 0;
            if (payloadUnitStart)
            {
                if (payload.Length == 0) return sections;
                var pointer = payload[0];
                pos = 1;
                if (_buffer.Count > 0 && pointer > 0 && pos + pointer <= payload.Length)
                {
                    Append(payload.Slice(pos, pointer));
                    EmitIfComplete(sections);
                }
                pos += pointer;
                _buffer.Clear(); _expected = null;
            }

            while (pos < payload.Length)
            {
                if (_buffer.Count == 0 && payload[pos] == 0xFF) break;
                if (_buffer.Count == 0 && payload.Length - pos >= 3)
                {
                    var sectionLength = ((payload[pos + 1] & 0x0F) << 8) | payload[pos + 2];
                    _expected = 3 + sectionLength;
                }
                var take = _expected.HasValue ? Math.Min(payload.Length - pos, _expected.Value - _buffer.Count) : payload.Length - pos;
                if (take <= 0) break;
                Append(payload.Slice(pos, take)); pos += take;
                if (EmitIfComplete(sections)) { _buffer.Clear(); _expected = null; }
                if (!_expected.HasValue && _buffer.Count >= 3)
                    _expected = 3 + (((_buffer[1] & 0x0F) << 8) | _buffer[2]);
            }
            return sections;
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            for (var i = 0; i < data.Length; i++) _buffer.Add(data[i]);
            if (!_expected.HasValue && _buffer.Count >= 3)
                _expected = 3 + (((_buffer[1] & 0x0F) << 8) | _buffer[2]);
        }

        private bool EmitIfComplete(List<byte[]> output)
        {
            if (!_expected.HasValue || _buffer.Count < _expected.Value) return false;
            output.Add(_buffer.Take(_expected.Value).ToArray());
            return true;
        }
    }
}
