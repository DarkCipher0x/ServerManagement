using System.IO.Compression;

namespace DcxManager.Vnc;

// RfbClient: the rectangle encodings (RFC 6143 §7.7). Every decoder writes only inside the rectangle the caller has
// already checked against the framebuffer, and every count it reads is bounded by that rectangle.
public sealed partial class RfbClient
{
    private ZrleFeed? _zrleFeed;
    private ZrleReader? _zrle;
    private readonly int[] _tile = new int[64 * 64];
    private readonly int[] _palette = new int[127];

    private void Decode(int enc, int x, int y, int w, int h)
    {
        switch (enc)
        {
            case 0: Raw(x, y, w, h); break;
            case 1: CopyRect(x, y, w, h); break;
            case 2: Rre(x, y, w, h); break;
            case 5: Hextile(x, y, w, h); break;
            case 16: Zrle(x, y, w, h); break;
            default: throw new RfbException($"The server used an encoding that wasn't asked for ({enc}).");
        }
    }

    private void Raw(int x, int y, int w, int h)
    {
        var row = new byte[w * 4];
        for (int j = 0; j < h; j++)
        {
            _in.Read(row);
            lock (Sync)
            {
                int at = (y + j) * Width + x;
                for (int i = 0; i < w; i++) Pixels[at + i] = unchecked((int)0xFF000000) | row[i * 4 + 2] << 16 | row[i * 4 + 1] << 8 | row[i * 4];
            }
        }
    }

    private void CopyRect(int x, int y, int w, int h)
    {
        int sx = _in.U16(), sy = _in.U16();
        if (sx + w > Width || sy + h > Height) throw new RfbException("The server copied from outside the screen.");
        lock (Sync)
        {
            // Rows in the right order so an overlapping copy reads each source row before it is overwritten.
            if (sy < y) for (int j = h - 1; j >= 0; j--) Array.Copy(Pixels, (sy + j) * Width + sx, Pixels, (y + j) * Width + x, w);
            else for (int j = 0; j < h; j++) Array.Copy(Pixels, (sy + j) * Width + sx, Pixels, (y + j) * Width + x, w);
        }
    }

    private void Fill(int x, int y, int w, int h, int px)
    {
        for (int j = 0; j < h; j++) Array.Fill(Pixels, px, (y + j) * Width + x, w);
    }

    private void Rre(int x, int y, int w, int h)
    {
        var n = _in.U32();
        if (n > (uint)w * h) throw new RfbException("RRE: more subrectangles than pixels.");
        int bg = _in.Pixel32();
        lock (Sync) Fill(x, y, w, h, bg);
        for (uint i = 0; i < n; i++)
        {
            int px = _in.Pixel32();
            int sx = _in.U16(), sy = _in.U16(), sw = _in.U16(), sh = _in.U16();
            if (sx + sw > w || sy + sh > h) throw new RfbException("RRE: a subrectangle is outside its rectangle.");
            lock (Sync) Fill(x + sx, y + sy, sw, sh, px);
        }
    }

    private void Hextile(int x, int y, int w, int h)
    {
        int bg = unchecked((int)0xFF000000), fg = bg;
        for (int ty = y; ty < y + h; ty += 16)
            for (int tx = x; tx < x + w; tx += 16)
            {
                int tw = Math.Min(16, x + w - tx), th = Math.Min(16, y + h - ty);
                int sub = _in.U8();
                if ((sub & 1) != 0) { Raw(tx, ty, tw, th); continue; }
                if ((sub & 2) != 0) bg = _in.Pixel32();
                if ((sub & 4) != 0) fg = _in.Pixel32();
                lock (Sync) Fill(tx, ty, tw, th, bg);
                if ((sub & 8) == 0) continue;
                int n = _in.U8();
                bool coloured = (sub & 16) != 0;
                for (int i = 0; i < n; i++)
                {
                    int px = coloured ? _in.Pixel32() : fg;
                    int xy = _in.U8(), wh = _in.U8();
                    int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                    if (sx + sw > tw || sy + sh > th) throw new RfbException("Hextile: a subrectangle is outside its tile.");
                    lock (Sync) Fill(tx + sx, ty + sy, sw, sh, px);
                }
            }
    }

    // ---- ZRLE: one zlib stream for the whole connection, 64×64 tiles ----------------------------------------------------

    private void Zrle(int x, int y, int w, int h)
    {
        var len = _in.U32();
        if (len > 64u << 20) throw new RfbException("ZRLE: a rectangle larger than 64 MB.");
        var data = _in.Bytes((int)len);
        _zrleFeed ??= new ZrleFeed();
        _zrle ??= new ZrleReader(new ZLibStream(_zrleFeed, CompressionMode.Decompress));
        _zrleFeed.Add(data);
        var z = _zrle;
        for (int ty = y; ty < y + h; ty += 64)
            for (int tx = x; tx < x + w; tx += 64)
            {
                int tw = Math.Min(64, x + w - tx), th = Math.Min(64, y + h - ty), count = tw * th;
                int sub = z.U8();
                if (sub == 0) { for (int i = 0; i < count; i++) _tile[i] = z.CPixel(); }
                else if (sub == 1) Array.Fill(_tile, z.CPixel(), 0, count);
                else if (sub is >= 2 and <= 16)
                {
                    var pal = _palette;
                    for (int i = 0; i < sub; i++) pal[i] = z.CPixel();
                    int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4, mask = (1 << bits) - 1;
                    for (int j = 0; j < th; j++)
                    {
                        int b = 0, left = 0;
                        for (int i = 0; i < tw; i++)
                        {
                            if (left == 0) { b = z.U8(); left = 8; }
                            left -= bits;
                            int idx = (b >> left) & mask;
                            if (idx >= sub) throw new RfbException("ZRLE: a palette index past the palette.");
                            _tile[j * tw + i] = pal[idx];
                        }
                    }
                }
                else if (sub == 128)
                {
                    for (int i = 0; i < count;)
                    {
                        int px = z.CPixel(), run = z.RunLength();
                        if (i + run > count) throw new RfbException("ZRLE: a run past the tile.");
                        Array.Fill(_tile, px, i, run); i += run;
                    }
                }
                else if (sub >= 130)
                {
                    int n = sub - 128;
                    var pal = _palette;
                    for (int i = 0; i < n; i++) pal[i] = z.CPixel();
                    for (int i = 0; i < count;)
                    {
                        int idx = z.U8(), run = 1;
                        if ((idx & 128) != 0) { idx &= 127; run = z.RunLength(); }
                        if (idx >= n) throw new RfbException("ZRLE: a palette index past the palette.");
                        if (i + run > count) throw new RfbException("ZRLE: a run past the tile.");
                        Array.Fill(_tile, pal[idx], i, run); i += run;
                    }
                }
                else throw new RfbException($"ZRLE: unknown tile type {sub}.");
                lock (Sync) for (int j = 0; j < th; j++) Array.Copy(_tile, j * tw, Pixels, (ty + j) * Width + tx, tw);
            }
        // Bytes the inflater hasn't taken yet (the flush marker at the end of the rectangle) stay queued: they belong to
        // the one zlib stream and are read before the next rectangle's data.
    }

    /// <summary>The compressed bytes of each ZRLE rectangle, appended as they arrive; the inflater reads them in order and
    /// keeps its state between rectangles (the server flushes at the end of each one).</summary>
    private sealed class ZrleFeed : Stream
    {
        private byte[] _data = Array.Empty<byte>();
        private int _pos;
        public int Remaining => _data.Length - _pos;
        public void Add(byte[] d) { _data = Remaining == 0 ? d : _data[_pos..].Concat(d).ToArray(); _pos = 0; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, Remaining);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Reads the decompressed ZRLE bytes through a buffer (kept between rectangles: the stream is continuous);
    /// running out inside a tile is a protocol error, never a wait.</summary>
    private sealed class ZrleReader(ZLibStream z)
    {
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _pos, _len;
        public int U8()
        {
            if (_pos >= _len)
            {
                try { _len = z.Read(_buf, 0, _buf.Length); }
                catch (InvalidDataException) { throw new RfbException("ZRLE: the compressed data is damaged."); }
                _pos = 0;
                if (_len <= 0) { _len = 0; throw new RfbException("ZRLE: the compressed data ended inside a tile."); }
            }
            return _buf[_pos++];
        }
        public int CPixel() { int b = U8(), g = U8(), r = U8(); return unchecked((int)0xFF000000) | r << 16 | g << 8 | b; }
        public int RunLength()
        {
            int run = 1, b;
            do { b = U8(); run += b; if (run > 64 * 64) throw new RfbException("ZRLE: a run longer than a tile."); } while (b == 255);
            return run;
        }
    }
}
