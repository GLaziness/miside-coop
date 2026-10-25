using System;
using System.Buffers.Binary;
using System.Text;
using UnityEngine;

namespace MiSideCoop.Net;

// Little-endian binary encoding for the co-op messages.
internal sealed class Writer
{
    private byte[] buf = new byte[256];
    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => buf.AsSpan(0, Length);
    public Writer Reset() { Length = 0; return this; }

    private Span<byte> Take(int n)
    {
        if (Length + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, Length + n));
        var s = buf.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public Writer U8(byte v) { Take(1)[0] = v; return this; }
    public Writer Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public Writer U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v); return this; }
    public Writer I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(Take(2), v); return this; }
    public Writer U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v); return this; }
    public Writer I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Take(4), v); return this; }
    public Writer U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v); return this; }
    public Writer F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(Take(4), v); return this; }

    public Writer Str(string s)
    {
        s ??= "";
        int n = Encoding.UTF8.GetByteCount(s);
        if (n > ushort.MaxValue) throw new ArgumentException("string too long");
        U16((ushort)n);
        Encoding.UTF8.GetBytes(s, Take(n));
        return this;
    }

    public Writer Vec3(Vector3 v) => F32(v.x).F32(v.y).F32(v.z);
    public Writer Quat(Quaternion q) => F32(q.x).F32(q.y).F32(q.z).F32(q.w);

    // A unit quaternion in 8 bytes: each component scaled to a signed 16-bit integer.
    public Writer QuatPacked(Quaternion q)
    {
        if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
        return I16(Pack(q.x)).I16(Pack(q.y)).I16(Pack(q.z)).I16(Pack(q.w));
    }

    private static short Pack(float f) => (short)Mathf.Clamp(Mathf.RoundToInt(f * 32767f), -32767, 32767);
}

internal ref struct Reader
{
    private readonly ReadOnlySpan<byte> data;
    private int pos;

    public Reader(ReadOnlySpan<byte> data) { this.data = data; pos = 0; }
    public int Remaining => data.Length - pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (pos + n > data.Length) throw new FormatException("message too short");
        var s = data.Slice(pos, n);
        pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public string Str() { int n = U16(); return Encoding.UTF8.GetString(Take(n)); }
    public Vector3 Vec3() => new(F32(), F32(), F32());
    public Quaternion Quat() => new(F32(), F32(), F32(), F32());

    public Quaternion QuatPacked()
    {
        var q = new Quaternion(I16() / 32767f, I16() / 32767f, I16() / 32767f, I16() / 32767f);
        float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
    }
}
