using System;
using System.IO;
using System.Text;

namespace SrvTvSetup
{
    // The app ships appended to this exe:
    //   [setup exe][zip][zip length: int64 LE][magic "SRVTVPK1"]
    // build-setup.ps1 writes it; the stub alone (no payload) doubles as the
    // uninstaller that is copied into the install folder.
    internal static class Payload
    {
        private const string Magic = "SRVTVPK1";
        private const int TrailerSize = 16;

        public static string SelfPath => System.Reflection.Assembly.GetExecutingAssembly().Location;

        // Returns (offset, length) of the zip inside this exe, or null if none.
        public static Tuple<long, long> Find()
        {
            using (var fs = File.OpenRead(SelfPath))
            {
                if (fs.Length < TrailerSize) return null;
                fs.Seek(-TrailerSize, SeekOrigin.End);
                var buf = new byte[TrailerSize];
                if (fs.Read(buf, 0, TrailerSize) != TrailerSize) return null;
                if (Encoding.ASCII.GetString(buf, 8, 8) != Magic) return null;
                var len = BitConverter.ToInt64(buf, 0);
                var offset = fs.Length - TrailerSize - len;
                if (len <= 0 || offset <= 0) return null;
                return Tuple.Create(offset, len);
            }
        }

        public static Stream Open(Tuple<long, long> where)
        {
            var fs = new FileStream(SelfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return new SubStream(fs, where.Item1, where.Item2);
        }

        // Copy of this exe without the payload (used as the uninstaller).
        public static void WriteStub(string dest)
        {
            var where = Find();
            using (var src = File.OpenRead(SelfPath))
            using (var dst = File.Create(dest))
            {
                var remaining = where == null ? src.Length : where.Item1;
                var buf = new byte[1 << 16];
                while (remaining > 0)
                {
                    var n = src.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0) break;
                    dst.Write(buf, 0, n);
                    remaining -= n;
                }
            }
        }

        private sealed class SubStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _start;
            private readonly long _length;
            private long _pos;

            public SubStream(Stream inner, long start, long length)
            {
                _inner = inner; _start = start; _length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _pos; set => _pos = value; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var left = _length - _pos;
                if (left <= 0) return 0;
                if (count > left) count = (int)left;
                _inner.Position = _start + _pos;
                var n = _inner.Read(buffer, offset, count);
                _pos += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: _pos = offset; break;
                    case SeekOrigin.Current: _pos += offset; break;
                    default: _pos = _length + offset; break;
                }
                return _pos;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
    }
}
